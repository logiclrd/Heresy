using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Single-owner incremental recursive sequencing and PCM rendering.
/// The dedicated PCM worker executes the generator and immediately applies
/// its events to the same PlaybackSession. The only lookahead is one cursor
/// step, never an event queue, a replay journal or a second producer.
/// This source MUST NOT be called by a device audio callback.
/// </summary>
public sealed class PreparedIncrementalAudioSource : IIncrementalArrangementSource, ICooperativeRenderStop, IDisposable
{
	private const int MaximumStepsPerRender = 1_000_000;
	private sealed record PendingEvent(long Frame, long Owner, NoteEvent Note);

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly SequencingCancellation? _cancellation;
	private readonly PlaybackSession _session;
	private readonly Action<NoteEvent>? _validateNote;
	private readonly bool _endInputAtNaturalCompletion;
	private readonly Action? _repeatRoot;
	private readonly Func<NoteEvent, long, long, NoteEvent>? _transform;
	private readonly Action? _afterRender;
	private Action<long>? _editorTraceBoundary;
	private Func<bool>? _editorTraceOriginCollectionEnabled;
	// F6-only backpressure. The source's pending UI row is a PCM segment
	// boundary, not a prefetched musical event or an early publication.
	private Func<long?>? _editorNextPositionBoundaryFrame;
	// Only one event may be prefetched. Cancellation invalidates it by
	// actual Pattern cursor ID rather than retaining a growing tombstone set.
	// The timeline can retire a just-started child while it is still
	// transforming the same NoteEvent (Start followed by Cut/Off).
	// The renderer must apply the event's Begin command *before* retiring
	// its controller, rather than losing retirement and leaking the lookup.
	private readonly List<long> _pendingRetiredScopes = [];
	private List<(long Owner, VirtualChannelIdentity Identity)>? _pendingVirtualFinalUses;
	private bool _applyingEvent;
	private IncrementalPatternTimelineStep? _deferredStep;
	private PendingEvent? _pendingEvent;
	private long _lastEventFrame = -1;
	private bool _complete;
	private bool _inputStopped;
	private bool _disposed;

	public PreparedIncrementalAudioSource(
		IncrementalRecursiveTimeline timeline, PlaybackSession session,
		Action<NoteEvent>? validatePreparedNote = null,
		Func<NoteEvent, long, long, NoteEvent>? prepareEvent = null,
		bool endInputAtNaturalCompletion = false,
		Action? repeatRoot = null,
		Action? afterRender = null,
		SequencingCancellation? cancellation = null)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_validateNote = validatePreparedNote;
		_endInputAtNaturalCompletion = endInputAtNaturalCompletion;
		_repeatRoot = repeatRoot;
		_transform = prepareEvent;
		_afterRender = afterRender;
		_cancellation = cancellation;
		_timeline.ScopeRetired += OnScopeRetired;
		_timeline.VirtualChannelFinalUse += OnVirtualChannelFinalUse;
		_timeline.ScopeCanceled += OnScopeCanceled;
		_timeline.RootPatternRowsEnded += OnRootPatternRowsEnded;
		_session.FlattenedCompletionStoppedProducer += OnFlattenedCompletionStoppedProducer;
		if (session.NextFrame != 0)
			throw new ArgumentException(
				"The incremental renderer requires a fresh playback session.",
				nameof(session));
		Format = new AudioOutputFormat(
			session.SampleRate, session.OutputChannelCount);
	}

	public void RequestStop() => _cancellation?.RequestStop();

	internal void CheckCancellation() => _cancellation?.ThrowIfCancellationRequested();

	public AudioOutputFormat Format { get; }
	public long NextFrame => _session.NextFrame;
	public PlaybackSession Session => _session;
	public bool IsComplete => _complete || _inputStopped;
	public TimeSpan LogicalDuration => _timeline.Elapsed;

	/// <summary>Only F6 editor-pattern audition installs a trace observer.
	/// The same PCM worker reports post-event and post-render boundaries
	/// in source-frame order, allowing very short notes inside one client
	/// buffer to have distinct lifetimes. No tracing during normal playback.</summary>
	internal void SetEditorTraceBoundary(Action<long> observer,
		Func<bool>? editorOriginCollectionEnabled = null,
		Func<long?>? nextPositionBoundaryFrame = null)
	{
		_editorTraceBoundary = observer
			?? throw new ArgumentNullException(nameof(observer));
		_editorTraceOriginCollectionEnabled = editorOriginCollectionEnabled;
		_editorNextPositionBoundaryFrame = nextPositionBoundaryFrame;
	}

	/// <summary>Produce at most the remaining logical arrangement frames.
	/// On reaching the natural end, return a short frame count so the
	/// offline renderer can transition directly to envelope tails.</summary>
	public int RenderLogical(int frameCount, Span<float> destination)
		=> RenderCore(frameCount, destination, stopAtLogicalEnd: true);

	/// <summary>
	/// End private child sequencing now, retaining its existing sounding
	/// voices and their normal end-input release/tail behavior. Called by
	/// the parent on this same PCM worker, at the exact musical boundary.
	/// </summary>
	/// <summary>Stop the private note producer without releasing or cutting
	/// voices that already exist. Used for source-level Fade: the parent
	/// controls the audible fade, while existing private voices keep their
	/// fade envelopes and future source notes never begin.</summary>
	public void StopProducing()
	{
		if (_inputStopped)
			return;
		_inputStopped = true;
		_pendingEvent = null;
		_deferredStep = null;
		_timeline.StopAllProducing();
		// One prefetched event may already have retired a source scope.
		// Discarding that event must still complete its retirement.
		FlushRetiredScopes();
	}

	public void EndInput() => EndInput(0, reconstructing: false);

	internal void EndInput(long elapsedReleaseFrames, bool reconstructing)
	{
		if (_session.InputEnded)
			return;
		StopProducing();
		_session.EndInput(elapsedReleaseFrames, reconstructing);
		_session.CutIndefiniteActiveVoicesAfterEndInput();
	}

	/// <summary>Reconstruct the events due at the selected PCM boundary
	/// without rendering or advancing a sample. Later events remain pending
	/// until the caller either resumes playback or stops the producer.</summary>
	internal void ApplyEventsAtCurrentFrame()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		long now = _session.NextFrame;
		for (int i = 0; i < MaximumStepsPerRender; i++)
		{
			CheckCancellation();
			FindNextEvent(now == long.MaxValue ? now : now + 1);
			if (_pendingEvent is not { } next || next.Frame != now)
				return;
			ApplyPendingEvent(next, now);
		}
		throw new InvalidOperationException("Recursive boundary exceeded the event budget.");
	}

	/// <summary>
	/// Live-preview commands enter on the same PCM worker and pass through the
	/// identical recursive-source transformation as scripted note starts.
	/// </summary>
	public void ApplyLiveEvent(ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(commands);
		long frame = _session.NextFrame;
		NoteEvent raw = new(
			new Heresy.Core.Timing.MusicalTime(
				FrameTime.FrameStartTime(frame, Format.SampleRate), 0),
			target, commands);
		NoteEvent prepared = _transform?.Invoke(raw, frame, -1) ?? raw;
		_session.ApplyLiveEvent(prepared.Target, prepared.Commands);
	}

	private void OnRootPatternRowsEnded(long invocationId, TimeSpan time)
	{
		// F6 loops the source rows, not its still-sounding notes or
		// recursively active children. Add the next invocation at this
		// same shared-clock boundary; all existing voices remain owned
		// by the uninterrupted PlaybackSession.
		if (!_inputStopped)
			_repeatRoot?.Invoke();
	}

	private void OnFlattenedCompletionStoppedProducer(long scopeId, long frame)
	{
		// Stop future notes on the same PCM worker. One already-prefetched
		// event can belong to the stopped subtree: invalidate it by its
		// Pattern cursor identity, not by physical output channel.
		List<long> stoppedCursors = [];
		_timeline.StopFlattenedSourceProducer(scopeId, stoppedCursors);
		if (_pendingEvent is { } pending && stoppedCursors.Contains(pending.Owner))
		{
			_pendingEvent = null;
			_lastEventFrame = Math.Min(_lastEventFrame, frame);
		}
		if (StepOwnedBy(_deferredStep, stoppedCursors))
			_deferredStep = null;
	}

	private static bool StepOwnedBy(IncrementalPatternTimelineStep? step,
		List<long> cursors)
		=> step switch
		{
			IncrementalPatternTimelineStep.Emit emit => cursors.Contains(emit.InvocationId),
			IncrementalPatternTimelineStep.Cooperate cooperation => cursors.Contains(cooperation.InvocationId),
			IncrementalPatternTimelineStep.Flow flow => cursors.Contains(flow.InvocationId),
			_ => false,
		};

	private void OnVirtualChannelFinalUse(long owner, VirtualChannelIdentity identity)
		=> (_pendingVirtualFinalUses ??= []).Add((owner, identity));

	private void FlushVirtualFinalUses()
	{
		// The timeline can relinquish its last reference while handing
		// the Emit to this source. Its renderer command must run first.
		// Editor backpressure may hold an Emit in _deferredStep instead.
		if (_applyingEvent || _pendingEvent is not null
			|| _deferredStep is IncrementalPatternTimelineStep.Emit
			|| _pendingVirtualFinalUses is not { Count: > 0 } pending)
			return;
		foreach (var item in pending)
			_session.RetireScopedVirtualChannel(item.Owner, item.Identity);
		pending.Clear();
	}

	private void OnScopeCanceled(long scopeId)
		=> _session.ApplyFlattenedScopeAction(
			scopeId, NoteDisplacementAction.Cut);

	private void OnScopeRetired(long scopeId)
		=> _pendingRetiredScopes.Add(scopeId);

	private void FlushRetiredScopes()
	{
		foreach (long scopeId in _pendingRetiredScopes)
			_session.RetirePhysicalScope(scopeId);
		_pendingRetiredScopes.Clear();
		FlushVirtualFinalUses();
	}

	public bool Cancel(long invocationId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_timeline.IsInvocationActive(invocationId))
			return false;
		List<long> canceledCursors = [];
		if (!_timeline.Cancel(invocationId, null, canceledCursors))
			return false;
		foreach (long cursor in canceledCursors)
			_session.CancelScopedVoices(cursor);
		// PendingEvent.Owner and scoped virtual channels both refer to
		// Pattern cursor IDs, not recursively nested invocation frame IDs.
		// Once a cursor is canceled, its sole prefetched event must never
		// be delivered, even when other roots remain active at that frame.
		if (_pendingEvent is { } pending
			&& canceledCursors.Contains(pending.Owner))
		{
			_pendingEvent = null;
			_lastEventFrame = Math.Min(_lastEventFrame, _session.NextFrame);
		}
		if (StepOwnedBy(_deferredStep, canceledCursors))
			_deferredStep = null;
		FlushRetiredScopes();
		return true;
	}

	private void FindNextEvent(long exclusiveFrame)
	{
		if (_pendingEvent is not null || _complete || _inputStopped)
			return;

		for (int i = 0; i < MaximumStepsPerRender; i++)
		{
			IncrementalPatternTimelineStep? step = _deferredStep;
			_deferredStep = null;
			if (step is null && !_timeline.TryStep(out step))
			{
				FlushRetiredScopes();
				if (_repeatRoot is not null)
				{
					_repeatRoot();
					continue;
				}
				_complete = true;
				return;
			}
			if (step is null)
				throw new InvalidOperationException(
					"Recursive timeline returned a null step.");

			// Script-editor F6 row positions must not accumulate for every
			// silent row inside a very large output buffer. Defer a generated
			// step ONLY if its own timestamp is at/after the next editor
			// presentation boundary. This applies to Flow/Cooperate as well as
			// Emit: deferring an earlier structural step could prevent an
			// audio event it enables from being discovered before rendering.
			if (_editorNextPositionBoundaryFrame?.Invoke() is long positionFrame
				&& positionFrame > _session.NextFrame
				&& positionFrame < exclusiveFrame
				&& FrameTime.Ceiling(step.Time, Format.SampleRate)
					>= positionFrame)
			{
				_deferredStep = step;
				return;
			}

			if (step is IncrementalPatternTimelineStep.Emit emit)
			{
				long frame = FrameTime.Ceiling(emit.Time, Format.SampleRate);
				if (frame < _lastEventFrame)
					throw new InvalidOperationException(
						"Incremental events must have nondecreasing timestamps.");
				if (frame < _session.NextFrame)
					throw new InvalidOperationException(
						"The recursive event fell behind the PCM render head.");
				_lastEventFrame = frame;
				_pendingEvent = new PendingEvent(
					frame, emit.InvocationId, emit.Note);
				return;
			}
			// An Emit can contain both Begin and Cut, and the timeline
			// may have retired the scope during transformation. Delay
			// retirement until that entire event has been applied. For
			// silent/flow steps there is no event to wait for.
			FlushRetiredScopes();

			// A cooperative/no-op step in the future is one bounded cursor
			// lookahead. Do not execute an unbounded silent script to reach
			// an event that lies past this output block.
			if (FrameTime.Ceiling(step.Time, Format.SampleRate) >= exclusiveFrame)
			{
				_deferredStep = step;
				return;
			}
		}
		throw new InvalidOperationException(
			"Recursive generation exceeded the per-render step budget.");
	}

	private void ApplyPendingEvent(PendingEvent next, long now)
	{
		_pendingEvent = null;
		_applyingEvent = true;
		try
		{
			_validateNote?.Invoke(next.Note);
			NoteEvent note = _transform?.Invoke(next.Note, now, next.Owner)
				?? next.Note;
			_session.ApplyScopedEvent(
				next.Owner, note.Target, note.Commands,
				note.PhysicalPlaybackOwner);
		}
		finally
		{
			_applyingEvent = false;
			FlushRetiredScopes();
		}
		// This event can start or end a voice before the next PCM
		// block boundary. Retain its real sample-frame deadline.
		_editorTraceBoundary?.Invoke(now);
	}

	public void Render(int frameCount, Span<float> destination)
		=> RenderCore(frameCount, destination, stopAtLogicalEnd: false);

	private int RenderCore(int frameCount, Span<float> destination,
		bool stopAtLogicalEnd)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		int channels = Format.ChannelCount;
		if (destination.Length != checked(frameCount * channels))
			throw new ArgumentException(
				"Destination must match the requested interleaved frames.",
				nameof(destination));
		long end = checked(_session.NextFrame + frameCount);
		int written = 0;
		while (_session.NextFrame < end)
		{
			long now = _session.NextFrame;
			FindNextEvent(end);
			if (stopAtLogicalEnd && _complete)
			{
				long logicalEnd = FrameTime.Ceiling(
					_timeline.Elapsed, Format.SampleRate);
				if (logicalEnd <= now)
					break;
				end = Math.Min(end, logicalEnd);
			}

			if (_pendingEvent is { } next && next.Frame == now)
			{
				ApplyPendingEvent(next, now);
				continue;
			}

			if (_endInputAtNaturalCompletion && _complete && !_inputStopped && !_session.InputEnded
				&& FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate) <= now)
				EndInput();

			long boundary = end;
			if (_pendingEvent is { } future)
				boundary = Math.Min(boundary, future.Frame);
			// F6-only presentation backpressure: render to the upcoming
			// due row/action boundary before looking ahead through more
			// silent rows. Unlike the audio event queue, this is metadata.
			if (_editorNextPositionBoundaryFrame?.Invoke() is long positionFrame
				&& positionFrame > now)
				boundary = Math.Min(boundary, positionFrame);
			if (_endInputAtNaturalCompletion && _complete && !_session.InputEnded && !_inputStopped)
				boundary = Math.Min(boundary,
					FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate));
			if (_editorTraceBoundary is not null
				&& (_editorTraceOriginCollectionEnabled?.Invoke() ?? true)
				&& _session.GetNextEditorOriginCompletionFrameExclusive(now)
					is long naturalEnd)
			{
				// Only editor-F6 rendering splits at a known actual
				// voice end. Ordinary PCM work stays on the original fast
				// path. The existing post-segment callback records the
				// transition at this precise sample-frame boundary.
				boundary = Math.Min(boundary, naturalEnd);
			}
			if (boundary <= now)
				throw new InvalidOperationException(
					"Incremental rendering could not advance its musical clock.");
			int count = checked((int)(boundary - now));
			_session.Render(now, count,
				destination.Slice(written * channels, count * channels));
			written += count;
			FlushVirtualFinalUses();
			// Detect natural voice retirement, completion of a flattened
			// producer, and release/fade, not just explicit Note commands.
			_editorTraceBoundary?.Invoke(_session.NextFrame);
		}
		_afterRender?.Invoke();
		return written;
	}

	/// <summary>The enclosing playback plan owns the timeline and session.</summary>
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timeline.ScopeRetired -= OnScopeRetired;
		_timeline.VirtualChannelFinalUse -= OnVirtualChannelFinalUse;
		_timeline.ScopeCanceled -= OnScopeCanceled;
		_timeline.RootPatternRowsEnded -= OnRootPatternRowsEnded;
		_session.FlattenedCompletionStoppedProducer -= OnFlattenedCompletionStoppedProducer;
	}
}
