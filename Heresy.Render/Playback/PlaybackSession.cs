using System;
using System.Buffers;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// Sequential schedule-to-PCM renderer. Physical channels own one current
/// voice each; displaced Continue/Off/Fade voices migrate into VirtualVoices.
/// </summary>
public sealed class PlaybackSession
{
	private readonly RenderContext _context;
	private readonly NoteSchedule _schedule;
	private readonly ISoundResolver _soundResolver;
	private readonly SortedDictionary<int, PlaybackChannelState> _channels = [];
	private readonly List<PlaybackVoice> _virtualVoices = [];

	private int _nextEventIndex;
	private long _nextFrame;
	private ulong _nextVoiceModulationSeed = 0x4845524553590001UL;
	private double _tempo = SequencingConstants.DefaultTempo;
	private int _speed = SequencingConstants.DefaultSpeed;

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

	public IReadOnlyList<PlaybackVoice> VirtualVoices => _virtualVoices;

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
		{
			foreach (NoteCommand command in noteEvent.Commands)
				ApplyGlobalCommand(command);
			return;
		}

		if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
		{
			throw new NotSupportedException(
				$"Playback target {noteEvent.Target.Kind} is not implemented yet.");
		}

		PlaybackChannelState channel = GetChannelState(noteEvent.Target.PhysicalChannel);

		foreach (NoteCommand command in noteEvent.Commands)
			ApplyCommand(channel, command, eventFrame, noteEvent.Offset.TimeOffset);
	}

	private void ApplyCommand(
		PlaybackChannelState channel,
		NoteCommand command,
		long eventFrame,
		TimeSpan eventTime)
	{
		switch (command)
		{
			case StartNoteCommand start:
				StartNote(channel, start, eventFrame);
				break;

			case NoteCutCommand:
				channel.CutCurrentVoice();
				break;

			case NoteOffCommand:
				channel.CurrentVoice?.ApplyNoteOff(
					eventFrame,
					_context.Configuration.SampleRate);
				CullFinishedCurrentVoice(channel, eventFrame);
				break;

			case SetNoteVolumeCommand volume:
				channel.SetNoteVolume(volume.Volume);
				break;

			case SetOverallChannelVolumeCommand volume:
				channel.SetOverallVolume(volume.Volume);
				break;

			case SetResonantFilterCommand filter:
				channel.SetFilterParameters(
					new ResonantFilterParameters(
						filter.Cutoff,
						filter.Resonance));
				break;

			case SetPlaybackOffsetCommand playbackOffset:
				if (channel.CurrentVoice is not null)
					channel.CurrentVoice.SoundState.PlaybackOffset = playbackOffset.Offset;
				break;

			case SetSourceFrameOffsetCommand sourceFrameOffset:
				if (channel.CurrentVoice is not null
					&& channel.CurrentVoice.Sound is ISourceFrameSeekableSound seekable)
				{
					seekable.SetSourceFrameOffset(
						channel.CurrentVoice.SoundState,
						sourceFrameOffset.SourceFrameOffset);
				}
				break;

			case AdjustPitchLinearUnitsCommand adjust:
				channel.CurrentVoice?.AdjustPitchLinearUnits(
					eventFrame,
					adjust.LinearUnits);
				break;

			case AdjustNoteVolumeCommand adjust:
				if (channel.CurrentVoice is not null)
				{
					double volume =
						channel.CurrentVoice.AdjustNoteVolume(
							eventFrame,
							adjust.TrackerUnits);
					channel.CaptureCurrentNoteVolume(volume);
				}
				else
				{
					channel.SetNoteVolume(
						Math.Clamp(
							channel.NoteVolume
								+ adjust.TrackerUnits / 64.0,
							0.0,
							1.0));
				}
				break;

			case SetTonePortamentoCommand tonePortamento:
				if (channel.CurrentVoice is null
					&& tonePortamento.TargetNote is not null)
				{
					StartNote(
						channel,
						tonePortamento.TargetNote,
						eventFrame);
				}

				channel.CurrentVoice?.SetTonePortamento(
					eventFrame,
					_tempo,
					tonePortamento.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					tonePortamento.LinearUnitsPerTick,
					tonePortamento.TargetNote?.PitchMultiplier,
					tonePortamento.Glissando);
				break;

			case ClearTonePortamentoCommand:
				channel.CurrentVoice?.ClearTonePortamento(eventFrame);
				break;

			case SetPitchSlideCommand slide:
				channel.CurrentVoice?.SetPitchSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.LinearUnitsPerTick);
				break;

			case ClearPitchSlideCommand:
				channel.CurrentVoice?.ClearPitchSlide(eventFrame);
				break;

			case SetNoteVolumeSlideCommand slide:
				channel.CurrentVoice?.SetNoteVolumeSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.TrackerUnitsPerTick);
				break;

			case ClearNoteVolumeSlideCommand:
				if (channel.CurrentVoice is not null)
				{
					double volume =
						channel.CurrentVoice.ClearNoteVolumeSlide(eventFrame);
					channel.SetNoteVolume(volume);
				}
				break;

			case RetriggerCurrentVoiceCommand retrigger:
				if (channel.CurrentVoice is not null)
				{
					PlaybackVoice voice = channel.CurrentVoice;
					voice.AddCutTo(channel.AntiClickTail);

					double volume = TrackerRetrigger.ApplyVolumeTransform(
						voice.GetBaseNoteVolume(eventFrame),
						retrigger.VolumeTransform);
					channel.SetNoteVolume(volume);

					voice.Retrigger(eventFrame);
				}
				break;

			case SetArpeggioCommand arpeggio:
				channel.CurrentVoice?.SetArpeggio(
					eventFrame,
					_tempo,
					_context.Configuration.SampleRate,
					arpeggio.FirstSemitones,
					arpeggio.SecondSemitones);
				break;

			case ClearArpeggioCommand:
				channel.CurrentVoice?.ClearArpeggio(eventFrame);
				break;

			case SetTremoloCommand tremolo:
				channel.CurrentVoice?.SetTremolo(
					eventFrame,
					eventTime,
					_tempo,
					_context.Configuration.SampleRate,
					tremolo.Speed,
					tremolo.Depth,
					tremolo.Waveform);
				break;

			case ClearTremoloCommand:
				channel.CurrentVoice?.ClearTremolo(
					eventFrame,
					eventTime);
				break;

			case SetVibratoCommand vibrato:
				channel.CurrentVoice?.SetVibrato(
					eventFrame,
					eventTime,
					_tempo,
					_context.Configuration.SampleRate,
					vibrato.Speed,
					vibrato.Depth,
					vibrato.Waveform);
				break;

			case ClearPitchModulationCommand:
				channel.CurrentVoice?.ClearPitchModulation(
					eventFrame,
					eventTime);
				break;

			case SetSpeedCommand:
				// PatternNoteProcessor has already baked speed into event timing.
				break;

			default:
				throw new NotSupportedException(
					$"Render command {command.GetType().Name} is not implemented yet.");
		}
	}

	private void ApplyGlobalCommand(NoteCommand command)
	{
		switch (command)
		{
			case SetTempoCommand tempo:
				_tempo = tempo.TicksPerDiachron;
				break;
			case SetSpeedCommand speed:
				_speed = speed.TicksPerRow;
				break;
		}
	}

	private void StartNote(
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame)
	{
		if (channel.CurrentVoice is not null)
		{
			channel.CaptureCurrentNoteVolume(
				channel.CurrentVoice.GetBaseNoteVolume(eventFrame));
		}

		DisplaceCurrentVoice(channel, eventFrame);

		if (!_soundResolver.TryResolve(start.SourceId, start.Mixdown, out ISound? sound)
			|| sound is null)
		{
			return;
		}

		NoteConfigurationSnapshot configuration =
			sound.SnapshotNoteConfiguration()
			?? throw new InvalidOperationException(
				$"{sound.GetType().Name}.{nameof(ISound.SnapshotNoteConfiguration)} returned null.");

		SoundState state = sound.CreateState();
		state.PitchMultiplier = start.PitchMultiplier;
		state.PlaybackSpeedMultiplier = start.PlaybackSpeedMultiplier;

		PlaybackVoice voice = new(
			sound,
			state,
			configuration,
			eventFrame,
			_context.Configuration.OutputChannelCount,
			_context.Configuration.SampleRate,
			channel.FilterParameters,
			channel.NoteVolume,
			channel.OverallVolume,
			_nextVoiceModulationSeed++);

		channel.AttachVoice(voice);
	}

	private void DisplaceCurrentVoice(
		PlaybackChannelState channel,
		long eventFrame)
	{
		PlaybackVoice? oldVoice = channel.DetachCurrentVoice();
		if (oldVoice is null)
			return;

		NewNotePolicy policy = oldVoice.Configuration.NewNotePolicy;

		switch (policy.Action)
		{
			case NewNoteAction.Cut:
				oldVoice.AddCutTo(channel.AntiClickTail);
				break;

			case NewNoteAction.Continue:
				_virtualVoices.Add(oldVoice);
				break;

			case NewNoteAction.Off:
				oldVoice.ApplyNoteOff(
					eventFrame,
					_context.Configuration.SampleRate);
				_virtualVoices.Add(oldVoice);
				break;

			case NewNoteAction.Fade:
				oldVoice.BeginFade(
					eventFrame,
					policy.FadeDuration,
					_context.Configuration.SampleRate);
				_virtualVoices.Add(oldVoice);
				break;

			default:
				throw new InvalidOperationException(
					$"Unsupported new-note action {policy.Action}.");
		}
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

				if (channel.CurrentVoice is not null)
				{
					bool finished = RenderVoice(
						channel.CurrentVoice,
						absoluteStartFrame,
						frameCount,
						channelBuffer);

					if (finished)
						channel.DetachCurrentVoice();
				}

				for (int frame = 0; frame < frameCount; frame++)
				{
					Span<float> outputFrame = channelBuffer.Slice(
						frame * outputChannelCount,
						outputChannelCount);
					channel.AntiClickTail.RenderFrame(outputFrame);
				}

				AddBuffer(destination, channelBuffer);
			}

			for (int index = 0; index < _virtualVoices.Count;)
			{
				PlaybackVoice voice = _virtualVoices[index];
				Span<float> voiceBuffer = rented.AsSpan(0, sampleCount);
				voiceBuffer.Clear();

				bool finished = RenderVoice(
					voice,
					absoluteStartFrame,
					frameCount,
					voiceBuffer);

				AddBuffer(destination, voiceBuffer);

				if (finished)
					_virtualVoices.RemoveAt(index);
				else
					index++;
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rented);
		}
	}

	private bool RenderVoice(
		PlaybackVoice voice,
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		long invocationStartFrame = absoluteStartFrame - voice.StartFrame;
		if (invocationStartFrame < 0)
			throw new InvalidOperationException("A playback voice began after the segment being rendered.");
long? soundEndRelative = voice.Sound.GetEndFrameExclusive(
			_context,
			voice.SoundState);

		long? effectiveEndAbsolute = soundEndRelative.HasValue
			? AddSaturating(voice.StartFrame, soundEndRelative.Value)
			: null;

		if (voice.FadeEndFrameExclusive.HasValue)
		{
			effectiveEndAbsolute = effectiveEndAbsolute.HasValue
				? Math.Min(effectiveEndAbsolute.Value, voice.FadeEndFrameExclusive.Value)
				: voice.FadeEndFrameExclusive.Value;
		}

		if (effectiveEndAbsolute.HasValue
			&& absoluteStartFrame >= effectiveEndAbsolute.Value)
		{
			return true;
		}

		int activeFrames = frameCount;
		if (effectiveEndAbsolute.HasValue)
		{
			long remaining = effectiveEndAbsolute.Value - absoluteStartFrame;
			activeFrames = (int)Math.Min(activeFrames, remaining);
		}

		int outputChannelCount = _context.Configuration.OutputChannelCount;
		Span<float> activeDestination = destination.Slice(
			0,
			checked(activeFrames * outputChannelCount));

		voice.Sound.Render(
			_context,
			voice.SoundState,
			invocationStartFrame,
			activeFrames,
			activeDestination);

		if (voice.SoundState.NaturalEndFrameExclusive.HasValue)
		{
			long discoveredRemaining =
				voice.SoundState.NaturalEndFrameExclusive.Value
				- invocationStartFrame;

			activeFrames = discoveredRemaining <= 0
				? 0
				: (int)Math.Min(activeFrames, discoveredRemaining);

			activeDestination = destination.Slice(
				0,
				checked(activeFrames * outputChannelCount));
		}

		for (int frame = 0; frame < activeFrames; frame++)
		{
			long absoluteFrame = absoluteStartFrame + frame;
			double volume =
				voice.GetNoteVolume(absoluteFrame)
				* voice.OverallVolume
				* voice.GetFadeGain(
					absoluteFrame,
					_context.Configuration.SampleRate);

			Span<float> outputFrame = activeDestination.Slice(
				frame * outputChannelCount,
				outputChannelCount);

			voice.FilterState.ProcessFrame(outputFrame);

			for (int outputChannel = 0; outputChannel < outputChannelCount; outputChannel++)
				outputFrame[outputChannel] = (float)(outputFrame[outputChannel] * volume);

			voice.ObserveOutputFrame(outputFrame);
		}

		if (voice.SoundState.NaturalEndFrameExclusive.HasValue
			&& invocationStartFrame + activeFrames
				>= voice.SoundState.NaturalEndFrameExclusive.Value)
		{
			return true;
		}

		return effectiveEndAbsolute.HasValue
			&& absoluteStartFrame + activeFrames >= effectiveEndAbsolute.Value;
	}

	private void CullFinishedCurrentVoice(
		PlaybackChannelState channel,
		long absoluteFrame)
	{
		PlaybackVoice? voice = channel.CurrentVoice;
		if (voice is null)
			return;

		long relativeFrame = Math.Max(0, absoluteFrame - voice.StartFrame);
		long? endFrame = voice.Sound.GetEndFrameExclusive(
			_context,
			voice.SoundState);

		if (endFrame.HasValue && relativeFrame >= endFrame.Value)
			channel.DetachCurrentVoice();
	}

	private static void AddBuffer(Span<float> destination, ReadOnlySpan<float> source)
	{
		for (int sample = 0; sample < destination.Length; sample++)
			destination[sample] += source[sample];
	}

	private static long AddSaturating(long left, long right)
	{
		if (right > 0 && left > long.MaxValue - right)
			return long.MaxValue;
		if (right < 0 && left < long.MinValue - right)
			return long.MinValue;
		return left + right;
	}
}
