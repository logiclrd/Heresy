using System;
using System.Collections.Generic;

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
	private sealed class NestedState : SoundState
	{
		public long SourceFrameOffset { get; set; }
	}

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly PlaybackSession _session;
	private readonly PreparedIncrementalAudioSource _source;
	private readonly Action _disposeChildren;
	private readonly List<float[]> _blocks = [];
	private readonly List<long> _blockStartFrames = [];
	private readonly int _sampleRate;
	private readonly int _channels;
	private long _preparedFrames;
	private long? _endFrame;

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
		if (nested.NoteOffTime.HasValue)
			return FrameTime.Ceiling(nested.NoteOffTime.Value, _sampleRate);
		return _endFrame.HasValue
			? Math.Max(0, _endFrame.Value - nested.SourceFrameOffset)
			: null;
	}

	/// <summary>
	/// Produce only private frames within the parent's already requested
	/// horizon; called exclusively by the non-audio preparation worker.
	/// </summary>
	public void PrepareThrough(long exclusiveEnd)
	{
		if (exclusiveEnd < 0)
			throw new ArgumentOutOfRangeException(nameof(exclusiveEnd));
		while (_preparedFrames < exclusiveEnd && !_endFrame.HasValue)
		{
			if (_source.IsPreparedToEnd && !_session.InputEnded
				&& _preparedFrames >= FrameTime.Ceiling(_timeline.Elapsed, _sampleRate))
			{
				_session.EndInput();
				_session.CutIndefiniteActiveVoicesAfterEndInput();
			}
			if (_session.IsQuiescent)
			{
				_endFrame = _preparedFrames;
				break;
			}
			long next = Math.Min(exclusiveEnd,
				checked(_preparedFrames + BlockFrames));
			if (_source.IsPreparedToEnd && !_session.InputEnded)
				next = Math.Min(next,
					FrameTime.Ceiling(_timeline.Elapsed, _sampleRate));
			if (next <= _preparedFrames)
			{
				// A completed timeline reaches its logical end at this
				// boundary. The next iteration releases its outstanding voices.
				continue;
			}
			_source.PrepareThrough(FrameTime.FrameStartTime(next, _sampleRate));
			// The first call can discover a shorter natural end.
			if (_source.IsPreparedToEnd && !_session.InputEnded)
			{
				long end = FrameTime.Ceiling(_timeline.Elapsed, _sampleRate);
				if (end > _preparedFrames)
					next = Math.Min(next, end);
			}
			int count = checked((int)(next - _preparedFrames));
			float[] output = new float[checked(count * _channels)];
			_source.Render(count, output);
			_blockStartFrames.Add(_preparedFrames);
			_blocks.Add(output);
			_preparedFrames = next;
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
			if (_endFrame.HasValue && sourceFrame >= _endFrame.Value)
				break;
			if (sourceFrame >= _preparedFrames)
				throw new InvalidOperationException(
					"Nested mixdown PCM was not prepared before its audio callback.");
			// Blocks may be shorter than BlockFrames at each parent
			// horizon, so locate the absolute frame by its recorded origin.
			// Binary search avoids quadratic readback for long mixdowns.
			int index = _blockStartFrames.BinarySearch(sourceFrame);
			if (index < 0)
				index = ~index - 1;
			if (index < 0)
				throw new InvalidOperationException("Missing prepared mixdown block.");
			float[] block = _blocks[index];
			int blockFrame = checked((int)(sourceFrame - _blockStartFrames[index]));
			if (blockFrame >= block.Length / _channels)
				throw new InvalidOperationException("Missing prepared mixdown frame.");
			for (int channel = 0; channel < _channels; channel++)
				destination[frame * _channels + channel] +=
					block[blockFrame * _channels + channel];
		}
	}
}
