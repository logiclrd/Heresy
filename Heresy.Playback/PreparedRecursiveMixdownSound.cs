using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using Heresy.Core.Sequences;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// One invocation-local, private-clock recursive mixdown voice. The producer
/// owns its timeline and renders native speaker-channel PCM into immutable
/// completed blocks before publishing the parent's preparation frontier.
/// Playback callbacks only read the completed blocks: no script or sequencing
/// code is ever executed by ISound.Render.
/// </summary>
/// <remarks>
/// Initially retains completed blocks so source-frame seeks are exact within
/// the prepared window. A future bounded history/replay policy must replace
/// this accumulation for indefinitely advancing voices.
/// </remarks>
internal sealed class PreparedRecursiveMixdownSound : ISound, ISourceFrameSeekableSound, IDisposable
{
	private const int BlockFrames = 256;
	private enum LifecycleKind { Release, Cut, Fade }
	private sealed record LifecycleChange(long Frame, LifecycleKind Kind);
	private sealed class NestedState : SoundState
	{
		public long SourceFrameOffset { get; set; }
	}

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly PlaybackSession _session;
	private readonly PreparedIncrementalAudioSource _source;
	private readonly Action _disposeChildren;
	// Publish immutable arrays by atomic replacement. A consumer reading an
	// earlier version of the current chunk still sees its covered frames.
	private readonly ConcurrentDictionary<long, float[]> _blocks = new();
	private readonly int _sampleRate;
	private readonly int _channels;
	private long _preparedFrames;
	private long _endFrame = -1;
	private readonly List<LifecycleChange> _lifecycle = [];
	private int _nextLifecycle;
	private bool _released;

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
		_disposeChildren = disposeChildren ?? throw new ArgumentNullException(nameof(disposeChildren));
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
				"Private mixdown pitch/playback-speed transforms need a private timeline mapping.");
		return new SoundInvocation(this, CreateState(), SnapshotNoteConfiguration());
	}

	public void SetSourceFrameOffset(SoundState state, long sourceFrameOffset)
	{
		if (sourceFrameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
		((NestedState)state).SourceFrameOffset = sourceFrameOffset;
	}

	public long? GetEndFrameExclusive(RenderContext context, SoundState state)
	{
		NestedState nested = (NestedState)state;
		// Parent Note Off is forwarded into the child's input by the
		// preparation worker; it is not a hard stop of the entire cooked
		// signal. Child release envelopes and displaced voices may tail.
		long end = Volatile.Read(ref _endFrame);
		return end >= 0
			? Math.Max(0, end - nested.SourceFrameOffset)
			: null;
	}

	/// <summary>
	/// Propagate a parent-owned voice lifecycle change at an exact parent
	/// playback frame. These requests are submitted only by the same producer
	/// that advances the private timeline, not by the PCM callback.
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
		if (at < _preparedFrames)
			throw new InvalidOperationException(
				"Cannot apply private mixdown lifecycle behind the prepared PCM frontier.");
		if (_lifecycle.Count != 0 && at < _lifecycle[^1].Frame)
			throw new InvalidOperationException(
				"Private mixdown lifecycle commands must arrive chronologically.");
		_lifecycle.Add(new LifecycleChange(at, kind));
	}

	private void ApplyLifecycleAtCurrentFrame()
	{
		while (_nextLifecycle < _lifecycle.Count
			&& _lifecycle[_nextLifecycle].Frame == _preparedFrames)
		{
			LifecycleChange action = _lifecycle[_nextLifecycle++];
			switch (action.Kind)
			{
				case LifecycleKind.Cut:
					Volatile.Write(ref _endFrame, _preparedFrames);
					return;
				case LifecycleKind.Release:
					if (!_session.InputEnded)
					{
						_session.EndInput();
						_session.CutIndefiniteActiveVoicesAfterEndInput();
					}
					_released = true;
					break;
				case LifecycleKind.Fade:
					_session.RequestFadeOfActiveVoices();
					break;
			}
		}
	}

	/// <summary>
	/// Produce only private frames within the parent's already requested
	/// horizon; called exclusively by the non-audio preparation worker.
	/// </summary>
	public void PrepareThrough(long exclusiveEnd)
	{
		if (exclusiveEnd < 0)
			throw new ArgumentOutOfRangeException(nameof(exclusiveEnd));
		while (_preparedFrames < exclusiveEnd && Volatile.Read(ref _endFrame) < 0)
		{
			ApplyLifecycleAtCurrentFrame();
			if (Volatile.Read(ref _endFrame) >= 0)
				break;
			if (!_released && _source.IsPreparedToEnd && !_session.InputEnded
				&& _preparedFrames >= FrameTime.Ceiling(_timeline.Elapsed, _sampleRate))
			{
				_session.EndInput();
				_session.CutIndefiniteActiveVoicesAfterEndInput();
			}
			if (_session.IsQuiescent)
			{
				Volatile.Write(ref _endFrame, _preparedFrames);
				break;
			}
			// Never straddle chunk boundaries when extending a published
			// immutable block with newly prepared frames.
			long next = Math.Min(exclusiveEnd,
				checked((_preparedFrames / BlockFrames + 1) * BlockFrames));
			if (_nextLifecycle < _lifecycle.Count)
				next = Math.Min(next, _lifecycle[_nextLifecycle].Frame);
			if (!_released && _source.IsPreparedToEnd && !_session.InputEnded)
				next = Math.Min(next,
					FrameTime.Ceiling(_timeline.Elapsed, _sampleRate));
			if (next <= _preparedFrames)
			{
				// A completed timeline reaches its logical end at this
				// boundary. The next iteration releases its outstanding voices.
				continue;
			}
			if (!_released)
				_source.PrepareThrough(FrameTime.FrameStartTime(next, _sampleRate));
			// The first call can discover a shorter natural end.
			if (!_released && _source.IsPreparedToEnd && !_session.InputEnded)
			{
				long end = FrameTime.Ceiling(_timeline.Elapsed, _sampleRate);
				if (end > _preparedFrames)
					next = Math.Min(next, end);
			}
			int count = checked((int)(next - _preparedFrames));
			float[] output = new float[checked(count * _channels)];
			if (_released)
				_session.Render(_session.NextFrame, count, output);
			else
				_source.Render(count, output);
			long chunkId = _preparedFrames / BlockFrames;
			int chunkOffset = checked((int)(_preparedFrames % BlockFrames));
			float[] snapshot = new float[checked(BlockFrames * _channels)];
			if (_blocks.TryGetValue(chunkId, out float[]? earlier))
				Array.Copy(earlier, snapshot, earlier.Length);
			output.CopyTo(snapshot, chunkOffset * _channels);
			_blocks[chunkId] = snapshot;
			Volatile.Write(ref _preparedFrames, next);
		}
	}

	public void Dispose()
	{
		_source.Dispose();
		_timeline.Dispose();
		_disposeChildren();
	}

	public void Render(RenderContext context, SoundState state,
		long startFrame, int frameCount, Span<float> destination)
	{
		if (startFrame < 0 || frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (destination.Length != checked(frameCount * _channels))
			throw new ArgumentException("Wrong speaker PCM length.", nameof(destination));
		NestedState nested = (NestedState)state;
		long offset = checked(nested.SourceFrameOffset
			+ FrameTime.Ceiling(nested.PlaybackOffset, _sampleRate));
		long from = checked(startFrame + offset);
		for (int frame = 0; frame < frameCount; frame++)
		{
			long sourceFrame = checked(from + frame);
			long end = Volatile.Read(ref _endFrame);
			if (end >= 0 && sourceFrame >= end)
				break;
			if (sourceFrame >= Volatile.Read(ref _preparedFrames))
				throw new InvalidOperationException(
					"Nested mixdown PCM was not prepared before its audio callback.");
			if (!_blocks.TryGetValue(sourceFrame / BlockFrames, out float[]? block))
				throw new InvalidOperationException("Missing prepared mixdown block.");
			int sourceIndex = checked((int)(sourceFrame % BlockFrames) * _channels);
			for (int channel = 0; channel < _channels; channel++)
				destination[frame * _channels + channel] +=
					block[sourceIndex + channel];
		}
	}
}
