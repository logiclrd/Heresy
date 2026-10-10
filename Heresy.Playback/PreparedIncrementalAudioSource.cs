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
public sealed class PreparedIncrementalAudioSource : IIncrementalArrangementSource, IDisposable
{
	private const int MaximumStepsPerRender = 1_000_000;
	private sealed record PendingEvent(long Frame, long Owner, NoteEvent Note);

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly PlaybackSession _session;
	private readonly Action<NoteEvent>? _validateNote;
	private readonly bool _endInputAtNaturalCompletion;
	private readonly Action? _repeatRoot;
	private readonly Func<NoteEvent, long, long, NoteEvent>? _transform;
	private readonly Action? _afterRender;
	// Only one event may be prefetched. Cancellation invalidates it by
	// actual Pattern cursor ID rather than retaining a growing tombstone set.
	// The timeline can retire a just-started child while it is still
	// transforming the same NoteEvent (Start followed by Cut/Off).
	// The renderer must apply the event's Begin command *before* retiring
	// its controller, rather than losing retirement and leaking the lookup.
	private readonly List<long> _pendingRetiredScopes = [];
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
		Action? afterRender = null)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_validateNote = validatePreparedNote;
		_endInputAtNaturalCompletion = endInputAtNaturalCompletion;
		_repeatRoot = repeatRoot;
		_transform = prepareEvent;
		_afterRender = afterRender;
		_timeline.ScopeRetired += OnScopeRetired;
		_timeline.ScopeCanceled += OnScopeCanceled;
		if (session.NextFrame != 0)
			throw new ArgumentException(
				"The incremental renderer requires a fresh playback session.",
				nameof(session));
		Format = new AudioOutputFormat(
			session.SampleRate, session.OutputChannelCount);
	}

	public AudioOutputFormat Format { get; }
	public long NextFrame => _session.NextFrame;
	public PlaybackSession Session => _session;
	public bool IsComplete => _complete || _inputStopped;
	public TimeSpan LogicalDuration => _timeline.Elapsed;

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
	public void EndInput()
	{
		if (_inputStopped)
			return;
		_inputStopped = true;
		_session.EndInput();
		_session.CutIndefiniteActiveVoicesAfterEndInput();
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
			_pendingEvent = null;
		if (_deferredStep is IncrementalPatternTimelineStep.Cooperate cooperation
			&& canceledCursors.Contains(cooperation.InvocationId)
			|| _deferredStep is IncrementalPatternTimelineStep.Flow flow
				&& canceledCursors.Contains(flow.InvocationId))
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
				_pendingEvent = null;
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
					FlushRetiredScopes();
				}
				continue;
			}

			if (_endInputAtNaturalCompletion && _complete && !_inputStopped && !_session.InputEnded
				&& FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate) <= now)
				EndInput();

			long boundary = end;
			if (_pendingEvent is { } future)
				boundary = Math.Min(boundary, future.Frame);
			if (_endInputAtNaturalCompletion && _complete && !_session.InputEnded && !_inputStopped)
				boundary = Math.Min(boundary,
					FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate));
			if (boundary <= now)
				throw new InvalidOperationException(
					"Incremental rendering could not advance its musical clock.");
			int count = checked((int)(boundary - now));
			_session.Render(now, count,
				destination.Slice(written * channels, count * channels));
			written += count;
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
		_timeline.ScopeCanceled -= OnScopeCanceled;
	}
}
