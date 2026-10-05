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
	private sealed class ActiveGlobalVolumeSlide
	{
		public required double TrackerUnitsPerTick { get; init; }
		public required int RemainingTickTransitions { get; set; }
		public required double NextTickPosition { get; set; }
	}

	private readonly RenderContext _context;
	private readonly NoteSchedule _schedule;
	private readonly ISoundResolver _soundResolver;
	private readonly TrackerTickClock _tickClock;
	private readonly SortedDictionary<int, PlaybackChannelState> _channels = [];
	private readonly List<PlaybackVoice> _virtualVoices = [];
	private readonly SortedDictionary<int, ActiveGlobalVolumeSlide> _globalVolumeSlides = [];

	private int _nextEventIndex;
	private long _nextFrame;
	private ulong _nextVoiceModulationSeed = 0x4845524553590001UL;
	private double _tempo = SequencingConstants.DefaultTempo;
	private int _speed = SequencingConstants.DefaultSpeed;
	private double _globalVolume = 1.0;
	private double _globalVolumeAnchorTickPosition;
	private double _globalVolumeAnchorValue = 1.0;
	private double? _globalVolumeNextAnchorTickPosition;
	private double _globalVolumeNextAnchorValue = 1.0;

	public PlaybackSession(
		RenderContext context,
		NoteSchedule schedule,
		ISoundResolver soundResolver)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
		_soundResolver = soundResolver ?? throw new ArgumentNullException(nameof(soundResolver));
		_tickClock = new TrackerTickClock(
			_schedule,
			_context.Configuration.SampleRate);
	}

	public long NextFrame => _nextFrame;

	public double GlobalVolume => _globalVolume;

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
			ulong panbrelloSeed = unchecked(
				0x50414E4252454C4CUL
				+ 0x9E3779B97F4A7C15UL
					* ((ulong)(uint)channel + 1UL));

			state = new PlaybackChannelState(
				_context.Configuration.OutputChannelCount,
				_context.Configuration.SampleRate,
				_tickClock,
				panbrelloSeed);
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
				ApplyGlobalCommand(command, eventFrame);
			return;
		}

		if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
		{
			throw new NotSupportedException(
				$"Playback target {noteEvent.Target.Kind} is not implemented yet.");
		}

		int physicalChannel = noteEvent.Target.PhysicalChannel;
		PlaybackChannelState channel = GetChannelState(physicalChannel);

		foreach (NoteCommand command in noteEvent.Commands)
		{
			ApplyCommand(
				physicalChannel,
				channel,
				command,
				eventFrame,
				noteEvent.Offset.TimeOffset);
		}
	}

	private void ApplyCommand(
		int physicalChannel,
		PlaybackChannelState channel,
		NoteCommand command,
		long eventFrame,
		TimeSpan eventTime)
	{
		switch (command)
		{
			case StartNoteCommand start:
				StartNote(
					physicalChannel,
					channel,
					start,
					eventFrame);
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

			case SetGlobalVolumeCommand volume:
				SetGlobalVolume(eventFrame, volume.Volume);
				break;

			case AdjustGlobalVolumeCommand adjust:
				AdjustGlobalVolume(
					eventFrame,
					adjust.TrackerUnits);
				break;

			case SetGlobalVolumeSlideCommand slide:
				SetGlobalVolumeSlide(
					physicalChannel,
					eventFrame,
					slide.TicksPerRow ?? _speed,
					slide.TrackerUnitsPerTick);
				break;

			case ClearGlobalVolumeSlideCommand:
				ClearGlobalVolumeSlide(
					physicalChannel,
					eventFrame);
				break;

			case SetNoteVolumeCommand volume:
				channel.SetNoteVolume(volume.Volume);
				break;

			case SetOverallChannelVolumeCommand volume:
				channel.SetOverallVolume(
					eventFrame,
					volume.Volume);
				break;

			case AdjustOverallChannelVolumeCommand adjust:
				channel.AdjustOverallVolume(
					eventFrame,
					adjust.TrackerUnits);
				break;

			case SetOverallChannelVolumeSlideCommand slide:
				channel.SetOverallVolumeSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.TrackerUnitsPerTick);
				break;

			case ClearOverallChannelVolumeSlideCommand:
				channel.ClearOverallVolumeSlide(eventFrame);
				break;

			case SetSpatialPositionCommand position:
				channel.SetPosition(
					eventFrame,
					position.Position);
				break;

			case AdjustSpatialXCommand adjust:
				channel.AdjustSpatialX(
					eventFrame,
					adjust.DeltaX,
					adjust.MinimumX,
					adjust.MaximumX);
				break;

			case SetSpatialXSlideCommand slide:
				channel.SetSpatialXSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.SpatialUnitsPerTick,
					slide.MinimumX,
					slide.MaximumX);
				break;

			case ClearSpatialXSlideCommand:
				channel.ClearSpatialXSlide(eventFrame);
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
						physicalChannel,
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

			case SetTremorCommand tremor:
				channel.SetTremor(
					eventFrame,
					_tempo,
					tremor.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					tremor.OnTicks,
					tremor.OffTicks);
				break;

			case ClearTremorCommand:
				channel.ClearTremor();
				break;

			case SetPanbrelloWaveformCommand waveform:
				channel.SetPanbrelloWaveform(
					eventFrame,
					waveform.Waveform);
				break;

			case SetPanbrelloCommand panbrello:
				channel.SetPanbrello(
					eventFrame,
					_tempo,
					panbrello.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					panbrello.Speed,
					panbrello.Depth,
					panbrello.Waveform);
				break;

			case ClearPanbrelloCommand:
				channel.ClearPanbrello(eventFrame);
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
					vibrato.Waveform,
					vibrato.DepthScale);
				break;

			case ClearPitchModulationCommand:
				channel.CurrentVoice?.ClearPitchModulation(
					eventFrame,
					eventTime);
				break;

			case ApplyPastNoteActionCommand pastNote:
				ApplyPastNoteAction(
					physicalChannel,
					channel,
					pastNote.Action,
					eventFrame);
				break;

			case SetCurrentVoiceDisplacementActionCommand displacement:
				if (channel.CurrentVoice is not null)
				{
					NewNoteAction action =
						displacement.Action switch
						{
							NoteDisplacementAction.Cut =>
								NewNoteAction.Cut,
							NoteDisplacementAction.Continue =>
								NewNoteAction.Continue,
							NoteDisplacementAction.Off =>
								NewNoteAction.Off,
							NoteDisplacementAction.Fade =>
								NewNoteAction.Fade,
							_ => throw new InvalidOperationException(
								$"Unsupported displacement action {displacement.Action}."),
						};

					channel.CurrentVoice.SetNewNoteActionOverride(action);
				}
				break;

			case SetSpeedCommand:
				// PatternNoteProcessor has already baked speed into event timing.
				break;

			default:
				throw new NotSupportedException(
					$"Render command {command.GetType().Name} is not implemented yet.");
		}
	}

	private void ApplyGlobalCommand(
		NoteCommand command,
		long eventFrame)
	{
		switch (command)
		{
			case SetTempoCommand tempo:
				_tempo = tempo.TicksPerDiachron;
				break;

			case SetSpeedCommand speed:
				_speed = speed.TicksPerRow;
				break;

			case SetGlobalVolumeCommand volume:
				SetGlobalVolume(eventFrame, volume.Volume);
				break;
		}
	}

	private void StartNote(
		int physicalChannel,
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame)
	{
		channel.SynchronizeContinuousState(eventFrame);

		if (channel.CurrentVoice is not null)
		{
			channel.CaptureCurrentNoteVolume(
				channel.CurrentVoice.GetBaseNoteVolume(eventFrame));
		}

		DisplaceCurrentVoice(channel, eventFrame);
		channel.ResetPanbrelloOffsetForNewNote();

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
			_tickClock,
			_nextVoiceModulationSeed++,
			physicalChannel);

		channel.AttachVoice(voice, eventFrame);
	}

	private void ApplyPastNoteAction(
		int physicalChannel,
		PlaybackChannelState channel,
		TrackerPastNoteAction action,
		long eventFrame)
	{
		for (int index = _virtualVoices.Count - 1; index >= 0; index--)
		{
			PlaybackVoice voice = _virtualVoices[index];
			if (voice.OriginPhysicalChannel != physicalChannel)
				continue;

			switch (action)
			{
				case TrackerPastNoteAction.Cut:
					voice.AddCutTo(channel.AntiClickTail);
					_virtualVoices.RemoveAt(index);
					break;

				case TrackerPastNoteAction.Off:
					voice.ApplyNoteOff(
						eventFrame,
						_context.Configuration.SampleRate);
					break;

				case TrackerPastNoteAction.Fade:
					voice.RequestNoteFade(
						eventFrame,
						_context.Configuration.SampleRate);
					break;

				default:
					throw new InvalidOperationException(
						$"Unsupported past-note action {action}.");
			}
		}
	}

	private void DisplaceCurrentVoice(
		PlaybackChannelState channel,
		long eventFrame)
	{
		PlaybackVoice? oldVoice = channel.DetachCurrentVoice();
		if (oldVoice is null)
			return;

		NewNoteAction action =
			oldVoice.NewNoteActionOverride
			?? oldVoice.Configuration.NewNotePolicy.Action;

		switch (action)
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
				TimeSpan? fadeDuration =
					oldVoice.NewNoteActionOverride.HasValue
						? oldVoice.Configuration.NewNoteFadeDuration
						: oldVoice.Configuration.NewNotePolicy.FadeDuration;

				oldVoice.RequestNoteFade(
					eventFrame,
					_context.Configuration.SampleRate,
					fadeDuration);
				_virtualVoices.Add(oldVoice);
				break;

			default:
				throw new InvalidOperationException(
					$"Unsupported new-note action {action}.");
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

				if (channel.HasActiveContinuousState
					|| channel.HasActiveTremor)
				{
					for (int frame = 0; frame < frameCount; frame++)
					{
						long absoluteFrame =
							absoluteStartFrame + frame;
						channel.SynchronizeContinuousState(
							absoluteFrame);

						PlaybackVoice? voice = channel.CurrentVoice;
						channel.SynchronizeTremor(
							absoluteFrame,
							voice is not null);

						if (voice is null)
							continue;

						Span<float> outputFrame =
							channelBuffer.Slice(
								frame * outputChannelCount,
								outputChannelCount);

						bool finished = RenderVoice(
							voice,
							absoluteFrame,
							1,
							outputFrame);

						double tremorGain = channel.TremorGain;
						if (tremorGain != 1.0)
						{
							for (int outputChannel = 0;
								outputChannel < outputChannelCount;
								outputChannel++)
							{
								outputFrame[outputChannel] =
									(float)(
										outputFrame[outputChannel]
										* tremorGain);
							}
						}

						if (finished)
							channel.DetachCurrentVoice();
					}
				}
				else if (channel.CurrentVoice is not null)
				{
					channel.SynchronizeContinuousState(
						absoluteStartFrame);

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

			ApplyGlobalVolume(
				absoluteStartFrame,
				frameCount,
				outputChannelCount,
				destination);
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rented);
		}
	}

	private void SetGlobalVolume(
		long absoluteFrame,
		double volume)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(volume)
			|| double.IsInfinity(volume)
			|| volume < 0.0
			|| volume > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}

		SynchronizeGlobalVolume(absoluteFrame);
		_globalVolume = volume;
		PlanNextGlobalVolumeAnchor(
			_tickClock.GetTickPosition(absoluteFrame));
	}

	private void AdjustGlobalVolume(
		long absoluteFrame,
		double trackerUnits)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(trackerUnits)
			|| double.IsInfinity(trackerUnits))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerUnits));
		}

		SynchronizeGlobalVolume(absoluteFrame);
		_globalVolume = Math.Clamp(
			_globalVolume + trackerUnits / 128.0,
			0.0,
			1.0);
		PlanNextGlobalVolumeAnchor(
			_tickClock.GetTickPosition(absoluteFrame));
	}

	private void SetGlobalVolumeSlide(
		int physicalChannel,
		long absoluteFrame,
		int ticksPerRow,
		double trackerUnitsPerTick)
	{
		if (physicalChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannel));
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (double.IsNaN(trackerUnitsPerTick)
			|| double.IsInfinity(trackerUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerUnitsPerTick));
		}

		SynchronizeGlobalVolume(absoluteFrame);

		double startTickPosition =
			_tickClock.GetTickPosition(absoluteFrame);
		int transitions = Math.Max(0, ticksPerRow - 1);

		if (transitions == 0)
		{
			_globalVolumeSlides.Remove(physicalChannel);
		}
		else
		{
			_globalVolumeSlides[physicalChannel] =
				new ActiveGlobalVolumeSlide
				{
					TrackerUnitsPerTick = trackerUnitsPerTick,
					RemainingTickTransitions = transitions,
					NextTickPosition = startTickPosition + 1.0,
				};
		}

		PlanNextGlobalVolumeAnchor(startTickPosition);
	}

	private void ClearGlobalVolumeSlide(
		int physicalChannel,
		long absoluteFrame)
	{
		if (physicalChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannel));
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		SynchronizeGlobalVolume(absoluteFrame);
		_globalVolumeSlides.Remove(physicalChannel);
		PlanNextGlobalVolumeAnchor(
			_tickClock.GetTickPosition(absoluteFrame));
	}

	private void ApplyGlobalVolume(
		long absoluteStartFrame,
		int frameCount,
		int outputChannelCount,
		Span<float> destination)
	{
		for (int frame = 0; frame < frameCount; frame++)
		{
			long absoluteFrame = absoluteStartFrame + frame;
			SynchronizeGlobalVolume(absoluteFrame);

			if (_globalVolume == 1.0)
				continue;

			Span<float> outputFrame = destination.Slice(
				frame * outputChannelCount,
				outputChannelCount);

			for (int outputChannel = 0;
				outputChannel < outputChannelCount;
				outputChannel++)
			{
				outputFrame[outputChannel] =
					(float)(outputFrame[outputChannel] * _globalVolume);
			}
		}
	}

	private void SynchronizeGlobalVolume(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		double targetTickPosition =
			_tickClock.GetTickPosition(absoluteFrame);

		while (_globalVolumeNextAnchorTickPosition.HasValue
			&& _globalVolumeNextAnchorTickPosition.Value
				<= targetTickPosition + 1e-9)
		{
			double reachedTickPosition =
				_globalVolumeNextAnchorTickPosition.Value;

			_globalVolume = _globalVolumeNextAnchorValue;
			AdvanceGlobalVolumeSlidesAt(reachedTickPosition);
			PlanNextGlobalVolumeAnchor(reachedTickPosition);
		}

		if (_globalVolumeNextAnchorTickPosition.HasValue
			&& _globalVolumeNextAnchorTickPosition.Value
				> _globalVolumeAnchorTickPosition)
		{
			double fraction = Math.Clamp(
				(targetTickPosition
					- _globalVolumeAnchorTickPosition)
				/ (_globalVolumeNextAnchorTickPosition.Value
					- _globalVolumeAnchorTickPosition),
				0.0,
				1.0);

			_globalVolume =
				_globalVolumeAnchorValue
				+ (_globalVolumeNextAnchorValue
					- _globalVolumeAnchorValue)
					* fraction;
		}
		else
		{
			_globalVolume = _globalVolumeAnchorValue;
		}
	}

	private void AdvanceGlobalVolumeSlidesAt(
		double anchorTickPosition)
	{
		foreach (KeyValuePair<int, ActiveGlobalVolumeSlide> pair
			in _globalVolumeSlides)
		{
			ActiveGlobalVolumeSlide slide = pair.Value;
			if (slide.RemainingTickTransitions <= 0
				|| Math.Abs(
					slide.NextTickPosition
						- anchorTickPosition) > 1e-9)
			{
				continue;
			}

			slide.RemainingTickTransitions--;
			if (slide.RemainingTickTransitions > 0)
				slide.NextTickPosition += 1.0;
		}
	}

	private void PlanNextGlobalVolumeAnchor(
		double anchorTickPosition)
	{
		_globalVolumeAnchorTickPosition = anchorTickPosition;
		_globalVolumeAnchorValue = _globalVolume;

		double? earliest = null;
		foreach (ActiveGlobalVolumeSlide slide
			in _globalVolumeSlides.Values)
		{
			if (slide.RemainingTickTransitions <= 0)
				continue;

			if (!earliest.HasValue
				|| slide.NextTickPosition < earliest.Value)
			{
				earliest = slide.NextTickPosition;
			}
		}

		if (!earliest.HasValue)
		{
			_globalVolumeNextAnchorTickPosition = null;
			_globalVolumeNextAnchorValue = _globalVolume;
			return;
		}

		double targetVolume = _globalVolume;
		foreach (KeyValuePair<int, ActiveGlobalVolumeSlide> pair
			in _globalVolumeSlides)
		{
			ActiveGlobalVolumeSlide slide = pair.Value;
			if (slide.RemainingTickTransitions <= 0
				|| Math.Abs(
					slide.NextTickPosition
						- earliest.Value) > 1e-9)
			{
				continue;
			}

			targetVolume = Math.Clamp(
				targetVolume
					+ slide.TrackerUnitsPerTick / 128.0,
				0.0,
				1.0);
		}

		_globalVolumeNextAnchorTickPosition = earliest.Value;
		_globalVolumeNextAnchorValue = targetVolume;
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
