using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Experimental sample-accurate playback of a cooperatively prepared recursive
/// Pattern timeline. PrepareThrough is called by a single non-audio producer;
/// Render consumes only timestamped, already-prepared events. No Pattern,
/// Sequence, or Roslyn code runs from Render.
/// </summary>
public sealed class PreparedIncrementalAudioSource : IAudioOutputSource, IDisposable
{
	private const int MaximumStepsPerPreparation = 1_000_000;

	private sealed record PreparedEvent(
		long Frame, long InvocationId, NoteEvent? Note);

	private readonly IncrementalRecursiveTimeline _timeline;
	private PlaybackSession _session;
	private readonly Func<PlaybackSession>? _replaySessionFactory;
	private readonly ConcurrentDictionary<long, PreparedEvent> _replayEvents = new();
	private long _replayPublishedCount;
	private long _replayNextWrite;
	private long _replayRead;
	private readonly Action<NoteEvent>? _validatePreparedNote;
	private readonly Func<NoteEvent, long, long, NoteEvent>? _prepareEvent;
	private readonly Action<TimeSpan>? _prepareNested;
	private readonly ConcurrentQueue<PreparedEvent> _events = new();
	private readonly ConcurrentQueue<long> _cancellations = new();
	private readonly HashSet<long> _canceledOwners = [];
	private long _preparedThroughTicks;
	private long _lastEventFrame = -1;
	private bool _finished;
	private bool _disposed;

	public PreparedIncrementalAudioSource(
		IncrementalRecursiveTimeline timeline, PlaybackSession session,
		Action<NoteEvent>? validatePreparedNote = null,
		Func<NoteEvent, long, long, NoteEvent>? prepareEvent = null,
		Action<TimeSpan>? prepareNested = null,
		Func<PlaybackSession>? replaySessionFactory = null)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_validatePreparedNote = validatePreparedNote;
		_prepareEvent = prepareEvent;
		_prepareNested = prepareNested;
		_replaySessionFactory = replaySessionFactory;
		if (session.NextFrame != 0)
			throw new ArgumentException(
				"The incremental renderer requires a fresh playback session.",
				nameof(session));
		Format = new AudioOutputFormat(
			session.SampleRate, session.OutputChannelCount);
	}

	public AudioOutputFormat Format { get; }

	public long NextFrame => _session.NextFrame;

	/// <summary>Current consumer-owned playback state, replaced when rewinding a private clock.</summary>
	public PlaybackSession Session => _session;

	/// <summary>
	/// Restore a fresh consumer session and begin replaying already prepared,
	/// timestamped note events from frame zero. This never calls TryStep,
	/// enumerates scripts, or runs the producer's callback.
	/// The caller must serialize rewinds with all audio rendering.
	/// </summary>
	public void RewindForReplay()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(PreparedIncrementalAudioSource));
		if (_replaySessionFactory is null)
			throw new NotSupportedException("This source does not retain events for private replay.");
		if (!_cancellations.IsEmpty || _canceledOwners.Count != 0)
			throw new NotSupportedException(
				"Private rewind with canceled invocation scopes requires reconstructing cancellation state.");
		PlaybackSession fresh = _replaySessionFactory();
		if (fresh.NextFrame != 0 || fresh.SampleRate != Format.SampleRate
			|| fresh.OutputChannelCount != Format.ChannelCount)
			throw new InvalidOperationException("Private replay session does not match the prepared source.");
		_session = fresh;
		_replayRead = 0;
	}

	private bool TryPeekEvent(out PreparedEvent? next)
	{
		if (_replaySessionFactory is null)
			return _events.TryPeek(out next);
		if (_replayRead >= Volatile.Read(ref _replayPublishedCount))
		{
			next = null;
			return false;
		}
		if (!_replayEvents.TryGetValue(_replayRead, out next))
			throw new InvalidOperationException("Prepared replay event was not published.");
		return true;
	}

	private bool TryReadEvent(out PreparedEvent? next)
	{
		if (_replaySessionFactory is null)
			return _events.TryDequeue(out next);
		if (!TryPeekEvent(out next))
			return false;
		_replayRead++;
		return true;
	}

	public bool IsPreparedToEnd => Volatile.Read(ref _finished);

	public TimeSpan PreparedThrough => TimeSpan.FromTicks(
		Volatile.Read(ref _preparedThroughTicks));

	/// <summary>
	/// Prepare all emissions strictly before exclusiveEnd. This runs the
	/// recursive generators and must never be invoked on the audio callback.
	/// A step at or past exclusiveEnd is permitted to be staged early, but
	/// subsequent same-time steps are resolved by the next preparation.
	/// The producer must be single-threaded; Render may run concurrently.
	/// </summary>
	public void PrepareThrough(TimeSpan exclusiveEnd)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(PreparedIncrementalAudioSource));
		if (exclusiveEnd < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(exclusiveEnd));
		if (exclusiveEnd.Ticks <= Volatile.Read(ref _preparedThroughTicks))
			return;

		if (!Volatile.Read(ref _finished))
		{
			bool reachedBoundary = false;
			for (int i = 0; i < MaximumStepsPerPreparation; i++)
			{
				if (!_timeline.TryStep(out IncrementalPatternTimelineStep? step))
				{
					Volatile.Write(ref _finished, true);
					reachedBoundary = true;
					break;
				}
				if (step is null)
					throw new InvalidOperationException(
						"Recursive timeline returned a null step.");
				if (step is IncrementalPatternTimelineStep.Emit emit)
				{
					// Factory-supplied restrictions and source checks run here,
					// never on the PCM callback.
					_validatePreparedNote?.Invoke(emit.Note);
					long frame = FrameTime.Ceiling(
						emit.Time, Format.SampleRate);
					if (frame < _lastEventFrame)
						throw new InvalidOperationException(
							"Incremental events must have nondecreasing timestamps.");
					if (frame < _session.NextFrame)
						throw new InvalidOperationException(
							"Cannot prepare an incremental event behind the playback head.");
					_lastEventFrame = frame;
					NoteEvent prepared = _prepareEvent?.Invoke(emit.Note, frame, emit.InvocationId)
						?? emit.Note;
					PreparedEvent staged = new(
						frame, emit.InvocationId, prepared);
					if (_replaySessionFactory is not null)
					{
						long index = _replayNextWrite++;
						if (!_replayEvents.TryAdd(index, staged))
							throw new InvalidOperationException("Duplicate private replay event index.");
						Volatile.Write(ref _replayPublishedCount, index + 1);
					}
					else
						_events.Enqueue(staged);
				}
				if (step.Time >= exclusiveEnd)
				{
					reachedBoundary = true;
					break;
				}
			}
			if (!reachedBoundary)
				throw new InvalidOperationException(
					"Incremental preparation exceeded its bounded step budget.");
		}

		// Stage every private child's chronological note events before
		// publishing parent coverage. Child PCM is rendered just-in-time
		// by the audio consumer, never by this producer.
		_prepareNested?.Invoke(exclusiveEnd);

		// Publish the complete exclusive coverage only *after* all preceding
		// emissions have been queued. The callback never invokes TryStep.
		Volatile.Write(ref _preparedThroughTicks, exclusiveEnd.Ticks);
	}

	/// <summary>
	/// Explicitly cancel a live invocation subtree at the current playback
	/// frame. The producer must serialize this with Render and may only cancel
	/// when no lookahead remains beyond the playback head. This deliberately
	/// avoids rewriting previously published, sample-exact events.
	/// Natural completion does not cut voices.
	/// </summary>
	public bool Cancel(long invocationId)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(PreparedIncrementalAudioSource));
		if (!_timeline.IsInvocationActive(invocationId))
			return false;
		long now = _session.NextFrame;
		if (FrameTime.Ceiling(
			TimeSpan.FromTicks(Volatile.Read(ref _preparedThroughTicks)),
			Format.SampleRate) > now)
			throw new InvalidOperationException(
				"Cancel requires the prepared frontier to meet the playback head.");
		List<long> removed = [];
		if (!_timeline.Cancel(invocationId, removed))
			return false;
		foreach (long owner in removed)
			_cancellations.Enqueue(owner);
		return true;
	}

	public void Render(int frameCount, Span<float> destination)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(PreparedIncrementalAudioSource));
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		int channelCount = Format.ChannelCount;
		int sampleCount = checked(frameCount * channelCount);
		if (destination.Length != sampleCount)
			throw new ArgumentException(
				"Destination must match the requested audio frame count.",
				nameof(destination));
		long end = checked(_session.NextFrame + frameCount);
		// All frames in [NextFrame,end) have event timestamps strictly
		// before FrameStartTime(end), including fractional frame ceilings.
		long coverage = FrameTime.FrameStartTime(
			end, Format.SampleRate).Ticks;
		if (frameCount != 0
			&& !Volatile.Read(ref _finished)
			&& Volatile.Read(ref _preparedThroughTicks) < coverage)
			throw new InvalidOperationException(
				"Incremental audio must be prepared beyond the requested block.");

		// Cancellations are published only at the serialized playback frontier.
		// A raw step beyond that frontier may already have been staged:
		// skip its canceled owner without moving or reordering other events.
		while (_cancellations.TryDequeue(out long owner))
		{
			_canceledOwners.Add(owner);
			_session.CancelScopedVoices(owner);
		}

		int writtenFrames = 0;
		while (TryPeekEvent(out PreparedEvent? next)
			&& next.Frame < end)
		{
			long now = _session.NextFrame;
			if (next.Frame < now)
				throw new InvalidOperationException(
					"A prepared event precedes the current playback frame.");

			int segment = checked((int)(next.Frame - now));
			if (segment != 0)
			{
				_session.Render(now, segment,
					destination.Slice(writtenFrames * channelCount,
						segment * channelCount));
				writtenFrames += segment;
			}

			// Multiple emissions at one output frame keep their original
			// streaming order. Scoped broadcast eligibility still checks
			// StartFrame < current frame, not callback/enqueue order.
			while (TryPeekEvent(out next) && next.Frame == _session.NextFrame)
			{
				if (!TryReadEvent(out PreparedEvent? current))
					continue;
				if (!_canceledOwners.Contains(current.InvocationId)
					&& current.Note is { } note)
					_session.ApplyScopedEvent(current.InvocationId,
						note.Target, note.Commands);
			}
		}
		int remaining = frameCount - writtenFrames;
		if (remaining != 0)
			_session.Render(_session.NextFrame, remaining,
				destination.Slice(writtenFrames * channelCount,
					remaining * channelCount));
	}

	/// <summary>
	/// Only this adapter's lifetime ends. The caller continues to own and
	/// dispose the timeline and PlaybackSession independently.
	/// </summary>
	public void Dispose() => _disposed = true;
}
