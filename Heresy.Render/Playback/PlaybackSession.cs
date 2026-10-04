using System;
using System.Buffers;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// Sequential schedule-to-PCM renderer for physical playback channels.
/// </summary>
public sealed class PlaybackSession
{
	private readonly RenderContext _context;
	private readonly NoteSchedule _schedule;
	private readonly ISoundResolver _soundResolver;
	private readonly SortedDictionary<int, PlaybackChannelState> _channels = [];

	private int _nextEventIndex;
	private long _nextFrame;

	public PlaybackSession(
		RenderContext context,
		NoteSchedule schedule,
		ISoundResolver soundResolver)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
		_soundResolver = soundResolver ?? throw new ArgumentNullException(nameof(soundResolver));
	}

	public long NextFrame => _nextFrame;

	public bool TryGetChannelState(int channel, out PlaybackChannelState? state)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		return _channels.TryGetValue(channel, out state);
	}

	public PlaybackChannelState GetChannelState(int channel)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		if (!_channels.TryGetValue(channel, out PlaybackChannelState? state))
		{
			state = new PlaybackChannelState(
				_context.Configuration.OutputChannelCount,
				_context.Configuration.SampleRate);
			_channels.Add(channel, state);
		}

		return state;
	}

	/// <summary>
	/// Fills destination with the next sequential block of interleaved float PCM.
	/// The start frame must equal NextFrame; seeking will be layered separately.
	/// </summary>
	public void Render(
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		if (startFrame != _nextFrame)
		{
			throw new InvalidOperationException(
				$"PlaybackSession is sequential. Expected start frame {_nextFrame}, received {startFrame}.");
		}
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		int outputChannelCount = _context.Configuration.OutputChannelCount;
		int requiredSamples = checked(frameCount * outputChannelCount);
		if (destination.Length != requiredSamples)
		{
			throw new ArgumentException(
				"Destination length must exactly match frame count and output channel count.",
				nameof(destination));
		}

		destination.Clear();

		long blockEnd = checked(startFrame + frameCount);
		long cursor = startFrame;

		while (cursor < blockEnd)
		{
			ProcessEventsThrough(cursor);

			long nextEventFrame = GetNextEventFrame();
			long segmentEnd = nextEventFrame <= cursor
				? cursor
				: Math.Min(blockEnd, nextEventFrame);

			if (segmentEnd == cursor)
				continue;

			int segmentFrames = checked((int)(segmentEnd - cursor));
			int destinationFrameOffset = checked((int)(cursor - startFrame));
			Span<float> segmentDestination = destination.Slice(
				checked(destinationFrameOffset * outputChannelCount),
				checked(segmentFrames * outputChannelCount));

			RenderSegment(cursor, segmentFrames, segmentDestination);
			cursor = segmentEnd;
		}

		// Events exactly at the end boundary affect the next frame and therefore
		// are intentionally left for the next Render call.
		_nextFrame = blockEnd;
	}

	private void ProcessEventsThrough(long frame)
	{
		while (_nextEventIndex < _schedule.Count)
		{
			NoteEvent noteEvent = _schedule[_nextEventIndex];
			long eventFrame = FrameTime.Ceiling(
				noteEvent.Offset.TimeOffset,
				_context.Configuration.SampleRate);

			if (eventFrame > frame)
				break;

			ApplyEvent(noteEvent, Math.Max(eventFrame, 0));
			_nextEventIndex++;
		}
	}

	private long GetNextEventFrame()
	{
		if (_nextEventIndex >= _schedule.Count)
			return long.MaxValue;

		return FrameTime.Ceiling(
			_schedule[_nextEventIndex].Offset.TimeOffset,
			_context.Configuration.SampleRate);
	}

	private void ApplyEvent(NoteEvent noteEvent, long eventFrame)
	{
		if (noteEvent.Target.Kind == ChannelTargetKind.Global)
			return;

		if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
		{
			throw new NotSupportedException(
				$"Playback target {noteEvent.Target.Kind} is not implemented yet.");
		}

		PlaybackChannelState channel = GetChannelState(noteEvent.Target.PhysicalChannel);

		foreach (NoteCommand command in noteEvent.Commands)
			ApplyCommand(channel, command, eventFrame);
	}

	private void ApplyCommand(
		PlaybackChannelState channel,
		NoteCommand command,
		long eventFrame)
	{
		switch (command)
		{
			case StartNoteCommand start:
				StartNote(channel, start, eventFrame);
				break;

			case NoteCutCommand:
				channel.CutCurrentSound();
				break;

			case NoteOffCommand:
				ApplyNoteOff(channel, eventFrame);
				break;

			case SetNoteVolumeCommand volume:
				channel.NoteVolume = volume.Volume;
				break;

			case SetOverallChannelVolumeCommand volume:
				channel.OverallVolume = volume.Volume;
				break;

			case SetPlaybackOffsetCommand playbackOffset:
				if (channel.CurrentSoundState is not null)
					channel.CurrentSoundState.PlaybackOffset = playbackOffset.Offset;
				break;

			case SetTempoCommand:
			case SetSpeedCommand:
				// PatternNoteProcessor has already baked these into event timing.
				break;

			default:
				throw new NotSupportedException(
					$"Render command {command.GetType().Name} is not implemented yet.");
		}
	}

	private void StartNote(
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame)
	{
		channel.CutCurrentSound();

		if (!_soundResolver.TryResolve(start.SourceId, start.Mixdown, out ISound? sound)
			|| sound is null)
		{
			return;
		}

		SoundState state = sound.CreateState();
		state.PitchMultiplier = start.PitchMultiplier;
		state.PlaybackSpeedMultiplier = start.PlaybackSpeedMultiplier;

		channel.CurrentSound = sound;
		channel.CurrentSoundState = state;
		channel.NoteStartFrame = eventFrame;
	}

	private void ApplyNoteOff(PlaybackChannelState channel, long eventFrame)
	{
		if (!channel.HasCurrentSound || channel.CurrentSoundState is null)
			return;

		long relativeFrame = Math.Max(0, eventFrame - channel.NoteStartFrame);
		channel.CurrentSoundState.NoteOffTime = FrameTime.FrameStartTime(
			relativeFrame,
			_context.Configuration.SampleRate);

		CullFinishedSound(channel, eventFrame);
	}

	private void RenderSegment(
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		int outputChannelCount = _context.Configuration.OutputChannelCount;
		int sampleCount = checked(frameCount * outputChannelCount);
		float[] rented = ArrayPool<float>.Shared.Rent(sampleCount);

		try
		{
			foreach (KeyValuePair<int, PlaybackChannelState> pair in _channels)
			{
				PlaybackChannelState channel = pair.Value;
				Span<float> channelBuffer = rented.AsSpan(0, sampleCount);
				channelBuffer.Clear();

				RenderCurrentSound(
					channel,
					absoluteStartFrame,
					frameCount,
					channelBuffer);

				for (int frame = 0; frame < frameCount; frame++)
				{
					Span<float> outputFrame = channelBuffer.Slice(
						frame * outputChannelCount,
						outputChannelCount);
					channel.AntiClickTail.RenderFrame(outputFrame);
				}

				for (int sample = 0; sample < sampleCount; sample++)
					destination[sample] += channelBuffer[sample];
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rented);
		}
	}

	private void RenderCurrentSound(
		PlaybackChannelState channel,
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		if (!channel.HasCurrentSound
			|| channel.CurrentSound is null
			|| channel.CurrentSoundState is null)
		{
			return;
		}

		long invocationStartFrame = absoluteStartFrame - channel.NoteStartFrame;
		if (invocationStartFrame < 0)
			throw new InvalidOperationException("A playback channel began after the segment being rendered.");

		long? soundEndFrame = channel.CurrentSound.GetEndFrameExclusive(
			_context,
			channel.CurrentSoundState);

		int activeFrames = frameCount;
		if (soundEndFrame.HasValue)
		{
			long remaining = soundEndFrame.Value - invocationStartFrame;
			if (remaining <= 0)
			{
				channel.DetachCurrentSound();
				return;
			}

			activeFrames = (int)Math.Min(activeFrames, remaining);
		}

		int outputChannelCount = _context.Configuration.OutputChannelCount;
		Span<float> activeDestination = destination.Slice(
			0,
			checked(activeFrames * outputChannelCount));

		channel.CurrentSound.Render(
			_context,
			channel.CurrentSoundState,
			invocationStartFrame,
			activeFrames,
			activeDestination);

		double volume = channel.NoteVolume * channel.OverallVolume;
		for (int frame = 0; frame < activeFrames; frame++)
		{
			Span<float> sourceFrame = activeDestination.Slice(
				frame * outputChannelCount,
				outputChannelCount);

			for (int outputChannel = 0; outputChannel < outputChannelCount; outputChannel++)
				sourceFrame[outputChannel] = (float)(sourceFrame[outputChannel] * volume);

			channel.ObserveSourceFrame(sourceFrame);
		}

		if (soundEndFrame.HasValue
			&& invocationStartFrame + activeFrames >= soundEndFrame.Value)
		{
			channel.DetachCurrentSound();
		}
	}

	private void CullFinishedSound(PlaybackChannelState channel, long absoluteFrame)
	{
		if (!channel.HasCurrentSound
			|| channel.CurrentSound is null
			|| channel.CurrentSoundState is null)
		{
			return;
		}

		long relativeFrame = Math.Max(0, absoluteFrame - channel.NoteStartFrame);
		long? endFrame = channel.CurrentSound.GetEndFrameExclusive(
			_context,
			channel.CurrentSoundState);

		if (endFrame.HasValue && relativeFrame >= endFrame.Value)
			channel.DetachCurrentSound();
	}
}
