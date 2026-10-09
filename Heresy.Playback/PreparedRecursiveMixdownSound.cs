using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

using Heresy.Core.Sequences;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// A mixdown voice owns an independent recursive clock and PCM session, but
/// never stores rendered audio. Its producer stages chronological events; the
/// parent PCM callback asks this child to render just the requested frames.
/// Descendant mixdowns recursively render into the same speaker layout.
/// </summary>
internal sealed class PreparedRecursiveMixdownSound :
	ISound, ISourceFrameSeekableSound, IDisposable
{
	private const int ScratchFrames = 256;
	private enum LifecycleKind { Release, Cut, Fade }
	private sealed record LifecycleChange(long Frame, LifecycleKind Kind);

	private sealed class NestedState : SoundState
	{
		public long SourceFrameOffset { get; set; }
	}

	private readonly IncrementalRecursiveTimeline _timeline;
	private PlaybackSession _session;
	private readonly PreparedIncrementalAudioSource _source;
	private readonly Action _disposeChildren;
	// Producer-published, immutable lifecycle history: consumer can replay
	// precisely the same changes after reconstructing a private session.
	private readonly ConcurrentDictionary<long, LifecycleChange> _lifecycle = new();
	private long _lifecycleNextWrite;
	private long _lifecyclePublished;
	private long _lifecycleRead;
	private long _renderOriginFrame;
	private readonly int _sampleRate;
	private readonly int _channels;

	// Only the producer writes these planning fields. The consumer never
	// examines its iterator, scripts or effect memory.
	private long _lastScheduledFrame = -1;
	private long _terminalInputFrame = long.MaxValue;
	private long _preparedThroughFrames;
	private long _naturalInputEndFrame = -1;
	private long _scheduledCutFrame = -1;

	// Only the audio consumer updates its session and observed sound end.
	private long _observedEndFrame = -1;
	private bool _disposed;

	public PreparedRecursiveMixdownSound(
		IncrementalRecursiveTimeline timeline,
		PlaybackSession session,
		PreparedIncrementalAudioSource source,
		long parentStartFrame,
		Action disposeChildren)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_disposeChildren = disposeChildren
			?? throw new ArgumentNullException(nameof(disposeChildren));
		if (parentStartFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(parentStartFrame));
		ParentStartFrame = parentStartFrame;
		_sampleRate = session.SampleRate;
		_channels = session.OutputChannelCount;
	}

	public long ParentStartFrame { get; }
	public SourceFrameSeekCost SeekCost => SourceFrameSeekCost.ReplayRequired;
	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> NoteConfigurationSnapshot.Default;
	public SoundState CreateState() => new NestedState();

	public SoundInvocation? CreateInvocation(
		double pitchMultiplier, double playbackSpeedMultiplier)
	{
		if (pitchMultiplier != 1 || playbackSpeedMultiplier != 1)
			throw new NotSupportedException(
				"Private mixdown pitch/playback-speed transforms need private-clock remapping.");
		return new SoundInvocation(this, CreateState(), SnapshotNoteConfiguration());
	}

	public void SetSourceFrameOffset(SoundState state, long sourceFrameOffset)
	{
		if (sourceFrameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
		((NestedState)state).SourceFrameOffset = sourceFrameOffset;
		// A new offset can rewind a previously observed natural ending.
		// Do not let the parent cull this voice before its next Render().
		Volatile.Write(ref _observedEndFrame, -1);
	}

	public long? GetEndFrameExclusive(RenderContext context, SoundState state)
	{
		NestedState nested = (NestedState)state;
		// Off is deliberately not a hard cooked-sound end: child envelopes,
		// releases and displaced voices may continue after its input stops.
		long cut = Volatile.Read(ref _scheduledCutFrame);
		long origin = nested.PlaybackOriginFrame;
		// Lifecycle operations preceding a retrigger belong to the
		// previous playback of this voice, not the restarted clock.
		if (cut < origin)
			cut = -1;
		long observed = Volatile.Read(ref _observedEndFrame);
		if (observed >= 0 && _renderOriginFrame == origin)
			observed = checked(observed + origin);
		else
			observed = -1;
		long end = cut < 0 ? observed
			: observed < 0 ? cut : Math.Min(cut, observed);
		return end < 0 ? null
			: Math.Max(0, end - nested.SourceFrameOffset
				- FrameTime.Ceiling(nested.PlaybackOffset, _sampleRate));
	}

	/// <summary>
	/// Only the producer schedules lifecycle commands. The audio consumer
	/// applies them at the matching private output frame, not during lookahead.
	/// </summary>
	public void ScheduleRelease(long parentFrame)
		=> Schedule(parentFrame, LifecycleKind.Release);

	public void ScheduleCut(long parentFrame)
		=> Schedule(parentFrame, LifecycleKind.Cut);

	public void ScheduleFade(long parentFrame)
		=> Schedule(parentFrame, LifecycleKind.Fade);

	private void Schedule(long parentFrame, LifecycleKind kind)
	{
		if (parentFrame < ParentStartFrame)
			throw new ArgumentOutOfRangeException(nameof(parentFrame));
		long at = parentFrame - ParentStartFrame;
		if (at < _lastScheduledFrame)
			throw new InvalidOperationException(
				"Private mixdown lifecycle commands must be chronological.");
		if (at < _session.NextFrame)
			throw new InvalidOperationException(
				"Cannot schedule a private mixdown lifecycle change behind its render head.");
		_lastScheduledFrame = at;
		if (kind is LifecycleKind.Release or LifecycleKind.Cut)
			_terminalInputFrame = Math.Min(_terminalInputFrame, at);
		if (kind == LifecycleKind.Cut)
			Volatile.Write(ref _scheduledCutFrame, at);
		long index = _lifecycleNextWrite++;
		if (!_lifecycle.TryAdd(index, new LifecycleChange(at, kind)))
			throw new InvalidOperationException("Duplicate private lifecycle index.");
		Volatile.Write(ref _lifecyclePublished, index + 1);
	}

	private bool TryPeekLifecycle(out LifecycleChange? next)
	{
		if (_lifecycleRead >= Volatile.Read(ref _lifecyclePublished))
		{
			next = null;
			return false;
		}
		if (!_lifecycle.TryGetValue(_lifecycleRead, out next))
			throw new InvalidOperationException(
				"Private lifecycle event was not published.");
		return true;
	}

	/// <summary>
	/// Translate producer-scheduled parent-relative frames to the *current*
	/// restarted child's local clock. Events before the restart are omitted.
	/// </summary>
	private bool TryPeekRelativeLifecycle(out LifecycleChange? next)
	{
		while (TryPeekLifecycle(out LifecycleChange? absolute)
			&& absolute is not null)
		{
			if (absolute.Frame < _renderOriginFrame)
			{
				_lifecycleRead++;
				continue;
			}
			next = absolute with { Frame = absolute.Frame - _renderOriginFrame };
			return true;
		}
		next = null;
		return false;
	}

	/// <summary>
	/// Prepare events only, including descendant timelines. This never calls
	/// the child's PlaybackSession.Render or changes its active voices.
	/// The parent publishes complete coverage after all children are ready.
	/// </summary>
	public void PrepareThrough(long exclusiveEnd)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(PreparedRecursiveMixdownSound));
		if (exclusiveEnd < 0)
			throw new ArgumentOutOfRangeException(nameof(exclusiveEnd));
		long prepareEnd = Math.Min(exclusiveEnd, _terminalInputFrame);
		if (prepareEnd > 0)
			_source.PrepareThrough(
				FrameTime.FrameStartTime(prepareEnd, _sampleRate));
		if (_source.IsPreparedToEnd)
			Volatile.Write(ref _naturalInputEndFrame,
				FrameTime.Ceiling(_timeline.Elapsed, _sampleRate));
		Volatile.Write(ref _preparedThroughFrames, exclusiveEnd);
	}

	public void Render(RenderContext context, SoundState state,
		long startFrame, int frameCount, Span<float> destination)
	{
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (destination.Length != checked(frameCount * _channels))
			throw new ArgumentException(
				"Destination must match interleaved speaker frame count.",
				nameof(destination));
		if (frameCount == 0)
			return;
		NestedState nested = (NestedState)state;
		long origin = nested.PlaybackOriginFrame;
		long offset = checked(nested.SourceFrameOffset
			+ FrameTime.Ceiling(nested.PlaybackOffset, _sampleRate));
		long first = checked(startFrame - origin + offset);
		if (first < 0)
			throw new InvalidOperationException(
				"Private mixdown playback cannot begin before its invocation origin.");
		long end = checked(first + frameCount);
		long cutAbsolute = Volatile.Read(ref _scheduledCutFrame);
		long knownCut = cutAbsolute >= origin
			? cutAbsolute - origin : -1;
		if (knownCut >= 0 && first >= knownCut)
			return;
		if (Volatile.Read(ref _preparedThroughFrames) < end)
			throw new InvalidOperationException(
				"Private mixdown events were not prepared for the requested audio frames.");
		if (origin != _renderOriginFrame || first < _session.NextFrame)
		{
			// All source scripts and child timelines remain owned by the
			// producer. Only the consumer's renderer/event cursor rewinds.
			_source.RewindForReplay();
			_session = _source.Session;
			_renderOriginFrame = origin;
			_lifecycleRead = 0;
			Volatile.Write(ref _observedEndFrame, -1);
		}

		float[] scratchArray = ArrayPool<float>.Shared.Rent(
			checked(ScratchFrames * _channels));
		try
		{
			// Forward source-frame seeking advances the live private renderer
			// while discarding its output. No PCM history is retained.
			RenderUntil(first, Span<float>.Empty, first, scratchArray);
			RenderUntil(end, destination, first, scratchArray);
		}
		finally
		{
			ArrayPool<float>.Shared.Return(scratchArray);
		}
	}

	private void RenderUntil(long exclusiveEnd, Span<float> destination,
		long outputStart, float[] scratchArray)
	{
		while (_session.NextFrame < exclusiveEnd)
		{
			long now = _session.NextFrame;
			ApplyDueLifecycle(now);
			long scheduledCut = Volatile.Read(ref _scheduledCutFrame);
			long cut = scheduledCut >= _renderOriginFrame
				? scheduledCut - _renderOriginFrame : -1;
			if (cut >= 0 && now >= cut)
			{
				Volatile.Write(ref _observedEndFrame, now);
				return;
			}
			long naturalEnd = Volatile.Read(ref _naturalInputEndFrame);
			if (naturalEnd >= 0 && now >= naturalEnd && !_session.InputEnded)
			{
				_session.EndInput();
				_session.CutIndefiniteActiveVoicesAfterEndInput();
			}
			if (_session.IsQuiescent)
			{
				Volatile.Write(ref _observedEndFrame, now);
				return;
			}

			long until = Math.Min(exclusiveEnd, checked(now + ScratchFrames));
			if (TryPeekRelativeLifecycle(out LifecycleChange? pending)
				&& pending is not null)
			{
				if (pending.Frame < now)
					throw new InvalidOperationException(
						"Private lifecycle event fell behind its renderer.");
				until = Math.Min(until, pending.Frame);
			}
			if (naturalEnd >= 0 && !_session.InputEnded)
				until = Math.Min(until, naturalEnd);
			if (cut >= 0)
				until = Math.Min(until, cut);
			if (until <= now)
				throw new InvalidOperationException(
					"Private renderer could not advance past its lifecycle boundary.");

			int count = checked((int)(until - now));
			Span<float> scratch = scratchArray.AsSpan(0, count * _channels);
			if (_session.InputEnded)
				_session.Render(now, count, scratch);
			else
				_source.Render(count, scratch);
			if (!destination.IsEmpty && until > outputStart)
			{
				long copyFrom = Math.Max(now, outputStart);
				int sourceSamples = checked((int)(copyFrom - now) * _channels);
				int sampleCount = checked((int)(until - copyFrom) * _channels);
				int destSamples = checked((int)(copyFrom - outputStart) * _channels);
				for (int i = 0; i < sampleCount; i++)
					destination[destSamples + i] += scratch[sourceSamples + i];
			}
		}
	}

	private void ApplyDueLifecycle(long frame)
	{
		while (TryPeekRelativeLifecycle(out LifecycleChange? change)
			&& change is not null && change.Frame == frame)
		{
			_lifecycleRead++;
			switch (change.Kind)
			{
				case LifecycleKind.Cut:
					Volatile.Write(ref _observedEndFrame, frame);
					return;
				case LifecycleKind.Release:
					if (!_session.InputEnded)
					{
						_session.EndInput();
						_session.CutIndefiniteActiveVoicesAfterEndInput();
					}
					break;
				case LifecycleKind.Fade:
					_session.RequestFadeOfActiveVoices();
					break;
			}
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_source.Dispose();
		_timeline.Dispose();
		_disposeChildren();
	}
}
