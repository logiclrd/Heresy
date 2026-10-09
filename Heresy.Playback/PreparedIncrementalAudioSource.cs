using System;
using System.Collections.Concurrent;
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
		long Frame, long InvocationId, NoteEvent Note);

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly PlaybackSession _session;
	private readonly ConcurrentQueue<PreparedEvent> _events = new();
	private long _preparedThroughTicks;
	private long _lastEventFrame = -1;
	private bool _finished;
	private bool _disposed;

	public PreparedIncrementalAudioSource(
		IncrementalRecursiveTimeline timeline, PlaybackSession session)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		if (session.NextFrame != 0)
			throw new ArgumentException(
				"The incremental renderer requires a fresh playback session.",
				nameof(session));
		Format = new AudioOutputFormat(
			session.SampleRate, session.OutputChannelCount);
	}

	public AudioOutputFormat Format { get; }

	public long NextFrame => _session.NextFrame;

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
		ObjectDisposedException.Throw(_disposed, this);
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
				if (step is IncrementalPatternTimelineStep.Emit emit)
				{
					long frame = FrameTime.Ceiling(
						emit.Time, Format.SampleRate);
					if (frame < _lastEventFrame)
						throw new InvalidOperationException(
							"Incremental events must have nondecreasing timestamps.");
					if (frame < _session.NextFrame)
						throw new InvalidOperationException(
							"Cannot prepare an incremental event behind the playback head.");
					_lastEventFrame = frame;
					_events.Enqueue(new PreparedEvent(
						frame, emit.InvocationId, emit.Note));
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

		// Publish the complete exclusive coverage only *after* all preceding
		// emissions have been queued. The callback never invokes TryStep.
		Volatile.Write(ref _preparedThroughTicks, exclusiveEnd.Ticks);
	}

	public void Render(int frameCount, Span<float> destination)
	{
		ObjectDisposedException.Throw(_disposed, this);
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

		int writtenFrames = 0;
		while (_events.TryPeek(out PreparedEvent? next)
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
			while (_events.TryPeek(out next) && next.Frame == _session.NextFrame)
			{
				if (!_events.TryDequeue(out PreparedEvent? current))
					continue;
				_session.ApplyScopedEvent(current.InvocationId,
					current.Note.Target, current.Note.Commands);
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
