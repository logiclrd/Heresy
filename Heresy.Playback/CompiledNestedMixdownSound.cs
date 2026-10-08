using System;
using System.Buffers;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// First integration slice for recursive sound sources: a pattern or sequence
/// whose private playback session renders directly into the parent's speaker
/// feeds. Each note owns a fresh session; no PCM buffer is materialized.
/// This is the mixdown form, not flattened child-channel sequencing.
/// </summary>
internal sealed class CompiledNestedMixdownSound
	: ISound, ISourceFrameSeekableSound
{
	private sealed class NestedState : SoundState
	{
		public PlaybackSession? Session { get; set; }
		public RenderContext? RenderContext { get; set; }
		public long SourceFrameOffset { get; set; }
		public bool HasCompleted { get; set; }
	}

	[ThreadStatic]
	private static HashSet<ObjectId>? _activeRenderSources;

	private readonly ObjectId _objectId;
	private readonly NoteSchedule _schedule;
	private readonly TimeSpan _logicalDuration;
	private readonly ISoundResolver _resolver;

	public CompiledNestedMixdownSound(
		ObjectId objectId,
		NoteSchedule schedule,
		TimeSpan logicalDuration,
		ISoundResolver resolver)
	{
		if (logicalDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(logicalDuration));
		_objectId = objectId;
		_schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
		_logicalDuration = logicalDuration;
		_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
	}

	public SourceFrameSeekCost SeekCost => SourceFrameSeekCost.ReplayRequired;

	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> NoteConfigurationSnapshot.Default;

	public SoundState CreateState() => new NestedState();

	public SoundInvocation? CreateInvocation(
		double pitchMultiplier,
		double playbackSpeedMultiplier)
	{
		// Outer pitch/timing transformations need a full nested-sequencer
		// timeline mapping. Do not silently play the wrong notes while that
		// (larger) contract is still outstanding.
		if (pitchMultiplier != 1.0 || playbackSpeedMultiplier != 1.0)
		{
			throw new NotSupportedException(
				"Nested mixdown pitch and playback-speed transforms are not implemented yet.");
		}
		return new SoundInvocation(
			this, CreateState(), SnapshotNoteConfiguration());
	}

	public void SetSourceFrameOffset(
		SoundState state,
		long sourceFrameOffset)
	{
		if (sourceFrameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
		NestedState nested = ValidateState(state);
		nested.SourceFrameOffset = sourceFrameOffset;
		nested.HasCompleted = false;
	}

	public long? GetEndFrameExclusive(
		RenderContext context,
		SoundState state)
	{
		ArgumentNullException.ThrowIfNull(context);
		NestedState nested = ValidateState(state);
		if (nested.NaturalEndFrameExclusive.HasValue)
			return nested.NaturalEndFrameExclusive;
		// Explicit Note Off should not leave a nested instrument stuck.
		// Full relative-time child release propagation is still outstanding.
		if (nested.NoteOffTime.HasValue)
			return FrameTime.Ceiling(
				nested.NoteOffTime.Value,
				context.Configuration.SampleRate);
		return null;
	}

	public void Render(
		RenderContext context,
		SoundState state,
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		ArgumentNullException.ThrowIfNull(context);
		NestedState nested = ValidateState(state);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		int channels = context.Configuration.OutputChannelCount;
		if (destination.Length != checked(frameCount * channels))
			throw new ArgumentException(
				"Destination has the wrong number of samples.", nameof(destination));
		if (frameCount == 0)
			return;

		HashSet<ObjectId> active = _activeRenderSources ??= [];
		if (!active.Add(_objectId))
		{
			throw new InvalidOperationException(
				$"Recursive pattern/sequence sound source cycle includes object {_objectId.Value}.");
		}
		try
		{
			RenderCore(context, nested, startFrame, frameCount, destination, channels);
		}
		finally
		{
			active.Remove(_objectId);
		}
	}

	private void RenderCore(
		RenderContext context,
		NestedState state,
		long startFrame,
		int frameCount,
		Span<float> destination,
		int channels)
	{
		long sourceOffset = checked(
			state.SourceFrameOffset
			+ FrameTime.Ceiling(
				state.PlaybackOffset, context.Configuration.SampleRate));
		long sourceStart = checked(startFrame + sourceOffset);
		PlaybackSession child = GetSession(context, state);
		if (child.NextFrame > sourceStart)
		{
			// Replay from frame zero to implement native source-frame
			// offset changes and backwards seeks correctly.
			state.Session = child = NewSession(context);
			state.HasCompleted = false;
		}

		float[] rented = ArrayPool<float>.Shared.Rent(checked(256 * channels));
		try
		{
			Advance(child, state, sourceStart, channels, rented, null,
				context.Configuration.SampleRate, sourceOffset);
			if (state.HasCompleted)
				return;
			Advance(child, state, checked(sourceStart + frameCount),
				channels, rented, destination,
				context.Configuration.SampleRate, sourceOffset);
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rented);
		}
	}

	private void Advance(
		PlaybackSession child,
		NestedState state,
		long targetFrame,
		int channels,
		float[] rented,
		Span<float> destination,
		int sampleRate,
		long sourceOffset)
	{
		long endInputFrame = FrameTime.Ceiling(_logicalDuration, sampleRate);
		long firstOutputFrame = targetFrame - destination.Length / channels;
		while (child.NextFrame < targetFrame && !state.HasCompleted)
		{
			if (!child.InputEnded && child.NextFrame >= endInputFrame)
			{
				child.EndInput();
				child.CutIndefiniteActiveVoicesAfterEndInput();
			}
			if (child.IsQuiescent)
			{
				state.HasCompleted = true;
				state.MarkNaturalEndReached(
					Math.Max(0, child.NextFrame - sourceOffset));
				break;
			}

			int count = (int)Math.Min(
				256L,
				targetFrame - child.NextFrame);
			if (!child.InputEnded)
				count = (int)Math.Min(count, endInputFrame - child.NextFrame);
			if (count <= 0)
				continue;

			long renderedFrom = child.NextFrame;
			Span<float> block = rented.AsSpan(0, checked(count * channels));
			child.Render(renderedFrom, count, block);
			if (!destination.IsEmpty)
			{
				int start = checked((int)(renderedFrom - firstOutputFrame));
				if (start < 0)
				{
					int skip = Math.Min(count, -start);
					block = block[(skip * channels)..];
					count -= skip;
					start = 0;
				}
				if (count > 0)
				{
					Span<float> outBlock =
						destination.Slice(start * channels, count * channels);
					for (int i = 0; i < outBlock.Length; i++)
						outBlock[i] += block[i];
				}
			}
		}
	}

	private PlaybackSession GetSession(RenderContext context, NestedState state)
	{
		if (state.Session is null || !ReferenceEquals(state.RenderContext, context))
		{
			state.Session = NewSession(context);
			state.RenderContext = context;
			state.HasCompleted = false;
		}
		return state.Session;
	}

	private PlaybackSession NewSession(RenderContext context)
		=> new(context, _schedule, _resolver);

	private static NestedState ValidateState(SoundState state)
		=> state as NestedState
			?? throw new ArgumentException(
				$"State must be created by {nameof(CompiledNestedMixdownSound)}.",
				nameof(state));
}
