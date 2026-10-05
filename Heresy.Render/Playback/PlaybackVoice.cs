using System;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// One live note voice. The same object may be attached to a physical playback
/// channel or migrated intact into the session's virtual-voice collection.
/// </summary>
public sealed class PlaybackVoice
{
	private sealed class ActiveVibrato
	{
		public required byte Speed { get; init; }
		public required TrackerWaveform Waveform { get; init; }
		public required double StartTickPosition { get; init; }
		public required byte StartPhase { get; init; }
		public required long RandomStartIndex { get; init; }
	}

	private sealed class ActiveTremolo
	{
		public required byte Speed { get; init; }
		public required TrackerWaveform Waveform { get; init; }
		public required double StartTickPosition { get; init; }
		public required byte StartPhase { get; init; }
		public required long RandomStartIndex { get; init; }
	}

	private sealed class ActiveNoteVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required double TrackerUnitsPerTick { get; init; }
		public required int TicksPerRow { get; init; }
	}

	private readonly float[] _previousOutputFrame;
	private readonly float[] _lastOutputFrame;
	private int _outputHistoryFrames;

	private long? _fadeStartFrame;
	private long? _fadeEndFrameExclusive;
	private TimeSpan? _fadeDuration;
	private bool _noteFadeRequested;
	private NewNoteAction? _newNoteActionOverride;

	private readonly ulong _modulationSeed;
	private readonly TrackerTickClock _tickClock;

	private byte _vibratoPhase;
	private long _vibratoRandomAnchorIndex = -1;
	private ActiveVibrato? _activeVibrato;

	private byte _tremoloPhase;
	private long _tremoloRandomAnchorIndex = -1;
	private ActiveTremolo? _activeTremolo;
	private TrackerTremoloVolumeCurve? _tremoloVolumeCurve;
	private long _tremoloVolumeCurveStartFrame;

	private ActiveNoteVolumeSlide? _activeNoteVolumeSlide;

	private PitchCurve _basePitchCurve = new ConstantPitchCurve(1.0);
	private long _basePitchCurveStartFrame;
	private double? _tonePortamentoTargetBaseMultiplier;
	private TrackerTonePortamentoCurve? _activeTonePortamentoCurve;
	private long _activeTonePortamentoCurveStartFrame;
	private double? _tonePortamentoContinuationBaseMultiplier;

	private PitchCurve _vibratoPitchCurve = new ConstantPitchCurve(1.0);
	private long _vibratoPitchCurveStartFrame;
	private PitchCurve _arpeggioPitchCurve = new ConstantPitchCurve(1.0);
	private long _arpeggioPitchCurveStartFrame;

	internal PlaybackVoice(
		ISound sound,
		SoundState soundState,
		NoteConfigurationSnapshot configuration,
		long startFrame,
		int outputChannelCount,
		int sampleRate,
		ResonantFilterParameters filterParameters,
		double noteVolume,
		double overallVolume,
		TrackerTickClock tickClock,
		ulong modulationSeed,
		int originPhysicalChannel)
	{
		Sound = sound ?? throw new ArgumentNullException(nameof(sound));
		SoundState = soundState ?? throw new ArgumentNullException(nameof(soundState));
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));
		if (originPhysicalChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(originPhysicalChannel));

		StartFrame = startFrame;
		OriginPhysicalChannel = originPhysicalChannel;
		NoteVolume = noteVolume;
		OverallVolume = overallVolume;
		_tickClock = tickClock
			?? throw new ArgumentNullException(nameof(tickClock));
		_modulationSeed = modulationSeed;

		_previousOutputFrame = new float[outputChannelCount];
		_lastOutputFrame = new float[outputChannelCount];
		FilterState = new ResonantFilterState(
			outputChannelCount,
			sampleRate,
			filterParameters);
	}

	public ISound Sound { get; }

	public SoundState SoundState { get; }

	public NoteConfigurationSnapshot Configuration { get; }

	public ResonantFilterState FilterState { get; }

	public long StartFrame { get; }

	/// <summary>
	/// Physical tracker channel on which this voice was originally started.
	/// This identity remains stable after NNA migration to a virtual voice.
	/// </summary>
	public int OriginPhysicalChannel { get; }

	public double NoteVolume { get; internal set; }

	public double OverallVolume { get; internal set; }

	public bool IsFading => _fadeStartFrame.HasValue;

	public bool IsNoteFadeRequested => _noteFadeRequested;

	internal NewNoteAction? NewNoteActionOverride
		=> _newNoteActionOverride;

	public long? FadeStartFrame => _fadeStartFrame;

	public long? FadeEndFrameExclusive => _fadeEndFrameExclusive;

	public TimeSpan? FadeDuration => _fadeDuration;

	internal void Retrigger(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		long relativeFrame = absoluteFrame - StartFrame;

		SoundState.PlaybackOffset = TimeSpan.Zero;
		if (Sound is ISourceFrameSeekableSound sourceFrameSeekable)
		{
			sourceFrameSeekable.SetSourceFrameOffset(
				SoundState,
				0);
		}

		SoundState.RestartPlaybackAt(relativeFrame);

		Array.Clear(_previousOutputFrame);
		Array.Clear(_lastOutputFrame);
		_outputHistoryFrames = 0;
	}

	internal void ApplyNoteOff(long absoluteFrame, int sampleRate)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		long relativeFrame = absoluteFrame - StartFrame;
		SoundState.NoteOffTime = FrameTime.FrameStartTime(
			relativeFrame,
			sampleRate);
	}

	internal void BeginFade(long absoluteFrame, TimeSpan duration, int sampleRate)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));

		_fadeStartFrame = absoluteFrame;
		_fadeDuration = duration;
		_fadeEndFrameExclusive = checked(
			absoluteFrame + FrameTime.Ceiling(duration, sampleRate));
	}

	internal void SetNewNoteActionOverride(
		NewNoteAction action)
	{
		if (!Enum.IsDefined(action))
			throw new ArgumentOutOfRangeException(nameof(action));

		_newNoteActionOverride = action;
	}

	internal void RequestNoteFade(
		long absoluteFrame,
		int sampleRate)
		=> RequestNoteFade(
			absoluteFrame,
			sampleRate,
			Configuration.NoteFadeDuration);

	internal void RequestNoteFade(
		long absoluteFrame,
		int sampleRate,
		TimeSpan? fadeDuration)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (fadeDuration.HasValue
			&& fadeDuration.Value <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(fadeDuration));
		}

		_noteFadeRequested = true;

		if (fadeDuration.HasValue)
		{
			BeginFade(
				absoluteFrame,
				fadeDuration.Value,
				sampleRate);
		}
	}

	internal double GetFadeGain(long absoluteFrame, int sampleRate)
	{
		if (!_fadeStartFrame.HasValue || !_fadeDuration.HasValue)
			return 1.0;

		long fadeStart = _fadeStartFrame.Value;
		if (absoluteFrame <= fadeStart)
			return 1.0;

		Int128 elapsed = (Int128)(absoluteFrame - fadeStart) * TimeSpan.TicksPerSecond;
		Int128 duration = (Int128)_fadeDuration.Value.Ticks * sampleRate;

		if (elapsed >= duration)
			return 0.0;

		return 1.0 - (double)elapsed / (double)duration;
	}

	internal void SetVibrato(
		long absoluteFrame,
		TimeSpan eventTime,
		double tempo,
		int sampleRate,
		byte speed,
		byte depth,
		TrackerWaveform waveform,
		double depthScale)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0) || double.IsNaN(tempo) || double.IsInfinity(tempo))
			throw new ArgumentOutOfRangeException(nameof(tempo));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));

		if (!(depthScale >= 0.0)
			|| double.IsNaN(depthScale)
			|| double.IsInfinity(depthScale))
		{
			throw new ArgumentOutOfRangeException(nameof(depthScale));
		}

		CommitVibratoPhaseThrough(absoluteFrame);

		long randomStartIndex = 0;
		if (waveform == TrackerWaveform.Random)
		{
			randomStartIndex =
				checked(++_vibratoRandomAnchorIndex);
		}

		_activeVibrato = new ActiveVibrato
		{
			Speed = speed,
			Waveform = waveform,
			StartTickPosition =
				_tickClock.GetTickPosition(absoluteFrame),
			StartPhase = _vibratoPhase,
			RandomStartIndex = randomStartIndex,
		};

		long relativeFrame = absoluteFrame - StartFrame;
		_vibratoPitchCurve = new TrackerVibratoPitchCurve(
			_vibratoPhase,
			speed,
			depth,
			_tickClock,
			absoluteFrame,
			waveform,
			_modulationSeed ^ 0x5649425241544F52UL,
			randomStartIndex,
			depthScale);
		_vibratoPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearPitchModulation(
		long absoluteFrame,
		TimeSpan eventTime)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitVibratoPhaseThrough(absoluteFrame);
		_activeVibrato = null;

		long relativeFrame = absoluteFrame - StartFrame;
		_vibratoPitchCurve = new ConstantPitchCurve(1.0);
		_vibratoPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void SetArpeggio(
		long absoluteFrame,
		double tempo,
		int sampleRate,
		byte firstSemitones,
		byte secondSemitones)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		long relativeFrame = absoluteFrame - StartFrame;
		_arpeggioPitchCurve = new TrackerArpeggioPitchCurve(
			firstSemitones,
			secondSemitones,
			_tickClock,
			absoluteFrame);
		_arpeggioPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearArpeggio(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		_arpeggioPitchCurve = new ConstantPitchCurve(1.0);
		_arpeggioPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void SetTremolo(
		long absoluteFrame,
		TimeSpan eventTime,
		double tempo,
		int sampleRate,
		byte speed,
		byte depth,
		TrackerWaveform waveform)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));

		CommitTremoloPhaseThrough(absoluteFrame);

		long randomStartIndex = 0;
		if (waveform == TrackerWaveform.Random)
		{
			randomStartIndex =
				checked(++_tremoloRandomAnchorIndex);
		}

		_activeTremolo = new ActiveTremolo
		{
			Speed = speed,
			Waveform = waveform,
			StartTickPosition =
				_tickClock.GetTickPosition(absoluteFrame),
			StartPhase = _tremoloPhase,
			RandomStartIndex = randomStartIndex,
		};

		_tremoloVolumeCurve = new TrackerTremoloVolumeCurve(
			_tremoloPhase,
			speed,
			depth,
			_tickClock,
			absoluteFrame,
			waveform,
			_modulationSeed ^ 0x5452454D4F4C4F00UL,
			randomStartIndex);
		_tremoloVolumeCurveStartFrame = absoluteFrame;
	}

	internal void ClearTremolo(
		long absoluteFrame,
		TimeSpan eventTime)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitTremoloPhaseThrough(absoluteFrame);
		_activeTremolo = null;
		_tremoloVolumeCurve = null;
		_tremoloVolumeCurveStartFrame = absoluteFrame;
	}

	internal void AdjustPitchLinearUnits(
		long absoluteFrame,
		double linearUnits)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(linearUnits)
			|| double.IsInfinity(linearUnits))
		{
			throw new ArgumentOutOfRangeException(nameof(linearUnits));
		}

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);
		double adjusted =
			currentBase
			* Math.Pow(
				2.0,
				linearUnits / TrackerVibrato.LinearSlideUnitsPerOctave);

		_activeTonePortamentoCurve = null;
		_tonePortamentoContinuationBaseMultiplier = null;

		_basePitchCurve = new ConstantPitchCurve(adjusted);
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void SetPitchSlide(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		double linearUnitsPerTick)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		}

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);

		_activeTonePortamentoCurve = null;
		_tonePortamentoContinuationBaseMultiplier = null;

		_basePitchCurve = new TrackerPitchSlideCurve(
			currentBase,
			linearUnitsPerTick,
			_tickClock,
			absoluteFrame,
			ticksPerRow);
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearPitchSlide(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);

		_basePitchCurve = new ConstantPitchCurve(currentBase);
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void SetTonePortamento(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		double linearUnitsPerTick,
		double? targetPitchMultiplier,
		bool glissando)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick)
			|| linearUnitsPerTick < 0.0)
		{
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		}

		if (targetPitchMultiplier.HasValue)
		{
			if (!(targetPitchMultiplier.Value > 0.0)
				|| double.IsNaN(targetPitchMultiplier.Value)
				|| double.IsInfinity(targetPitchMultiplier.Value))
			{
				throw new ArgumentOutOfRangeException(nameof(targetPitchMultiplier));
			}

			_tonePortamentoTargetBaseMultiplier =
				targetPitchMultiplier.Value / SoundState.PitchMultiplier;
		}

		if (!_tonePortamentoTargetBaseMultiplier.HasValue)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase =
			GetTonePortamentoContinuousBaseMultiplier(
				relativeFrame);

		TrackerTonePortamentoCurve curve = new(
			currentBase,
			_tonePortamentoTargetBaseMultiplier.Value,
			linearUnitsPerTick,
			_tickClock,
			absoluteFrame,
			ticksPerRow,
			glissando);

		_activeTonePortamentoCurve = curve;
		_activeTonePortamentoCurveStartFrame = relativeFrame;
		_tonePortamentoContinuationBaseMultiplier = null;

		_basePitchCurve = curve;
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearTonePortamento(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);

		if (_activeTonePortamentoCurve is not null)
		{
			_tonePortamentoContinuationBaseMultiplier =
				_activeTonePortamentoCurve.GetContinuousMultiplier(
					checked(
						relativeFrame
							- _activeTonePortamentoCurveStartFrame));
		}

		_activeTonePortamentoCurve = null;

		_basePitchCurve = new ConstantPitchCurve(currentBase);
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void SetNoteVolume(double volume)
	{
		NoteVolume = volume;
		_activeNoteVolumeSlide = null;
	}

	internal double AdjustNoteVolume(
		long absoluteFrame,
		double trackerUnits)
	{
		if (double.IsNaN(trackerUnits)
			|| double.IsInfinity(trackerUnits))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerUnits));
		}

		double adjusted = Math.Clamp(
			GetBaseNoteVolume(absoluteFrame) + trackerUnits / 64.0,
			0.0,
			1.0);

		SetNoteVolume(adjusted);
		return adjusted;
	}

	internal void SetNoteVolumeSlide(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		double trackerUnitsPerTick)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (double.IsNaN(trackerUnitsPerTick)
			|| double.IsInfinity(trackerUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerUnitsPerTick));
		}

		double current = GetBaseNoteVolume(absoluteFrame);
		NoteVolume = current;

		_activeNoteVolumeSlide = new ActiveNoteVolumeSlide
		{
			StartFrame = absoluteFrame,
			TrackerUnitsPerTick = trackerUnitsPerTick,
			TicksPerRow = ticksPerRow,
		};
	}

	internal double ClearNoteVolumeSlide(long absoluteFrame)
	{
		double current = GetBaseNoteVolume(absoluteFrame);
		NoteVolume = current;
		_activeNoteVolumeSlide = null;
		return current;
	}

	internal double GetBaseNoteVolume(long absoluteFrame)
	{
		ActiveNoteVolumeSlide? slide = _activeNoteVolumeSlide;
		if (slide is null)
			return NoteVolume;

		double rowTime = Math.Clamp(
			_tickClock.GetElapsedTicks(
				slide.StartFrame,
				Math.Max(slide.StartFrame, absoluteFrame)),
			0.0,
			slide.TicksPerRow);
		double legacyEquivalentTicks =
			rowTime
				* Math.Max(0, slide.TicksPerRow - 1)
				/ slide.TicksPerRow;

		return Math.Clamp(
			NoteVolume
				+ slide.TrackerUnitsPerTick
					* legacyEquivalentTicks / 64.0,
			0.0,
			1.0);
	}

	internal double GetNoteVolume(long absoluteFrame)
	{
		double volume = GetBaseNoteVolume(absoluteFrame);

		if (_tremoloVolumeCurve is null
			|| absoluteFrame < _tremoloVolumeCurveStartFrame)
		{
			return volume;
		}

		double offsetUnits =
			_tremoloVolumeCurve.GetOffsetTrackerUnits(
				absoluteFrame - _tremoloVolumeCurveStartFrame);

		return Math.Clamp(
			volume + offsetUnits / 64.0,
			0.0,
			1.0);
	}

	private double GetTonePortamentoContinuousBaseMultiplier(
		long relativeFrame)
	{
		if (_activeTonePortamentoCurve is not null)
		{
			return _activeTonePortamentoCurve.GetContinuousMultiplier(
				checked(
					relativeFrame
						- _activeTonePortamentoCurveStartFrame));
		}

		if (_tonePortamentoContinuationBaseMultiplier.HasValue)
		{
			return _tonePortamentoContinuationBaseMultiplier.Value;
		}

		return GetBasePitchMultiplier(relativeFrame);
	}

	private double GetBasePitchMultiplier(long relativeFrame)
		=> _basePitchCurve.GetMultiplier(
			checked(relativeFrame - _basePitchCurveStartFrame));

	private void RecomposePitchTrajectory(long relativeFrame)
	{
		PitchCurve baseCurve = new OffsetPitchCurve(
			_basePitchCurve,
			checked(relativeFrame - _basePitchCurveStartFrame));
		PitchCurve vibratoCurve = new OffsetPitchCurve(
			_vibratoPitchCurve,
			checked(relativeFrame - _vibratoPitchCurveStartFrame));
		PitchCurve arpeggioCurve = new OffsetPitchCurve(
			_arpeggioPitchCurve,
			checked(relativeFrame - _arpeggioPitchCurveStartFrame));

		SoundState.PitchTrajectory.SetCurve(
			relativeFrame,
			new ProductPitchCurve(
				baseCurve,
				new ProductPitchCurve(
					vibratoCurve,
					arpeggioCurve)));
	}

	private void CommitVibratoPhaseThrough(long absoluteFrame)
	{
		ActiveVibrato? vibrato = _activeVibrato;
		if (vibrato is null)
			return;

		double elapsedTicks = Math.Max(
			0.0,
			_tickClock.GetTickPosition(absoluteFrame)
				- vibrato.StartTickPosition);
		int phaseAdvance = checked(
			(int)Math.Round(
				elapsedTicks * vibrato.Speed * 4.0));

		_vibratoPhase = unchecked(
			(byte)(vibrato.StartPhase + phaseAdvance));

		if (vibrato.Waveform == TrackerWaveform.Random)
		{
			long completedAnchors =
				(long)Math.Floor(elapsedTicks + 1e-9);
			_vibratoRandomAnchorIndex =
				checked(
					vibrato.RandomStartIndex
						+ completedAnchors);
		}
	}

	private void CommitTremoloPhaseThrough(long absoluteFrame)
	{
		ActiveTremolo? tremolo = _activeTremolo;
		if (tremolo is null)
			return;

		double elapsedTicks = Math.Max(
			0.0,
			_tickClock.GetTickPosition(absoluteFrame)
				- tremolo.StartTickPosition);
		int phaseAdvance = checked(
			(int)Math.Round(
				elapsedTicks * tremolo.Speed * 4.0));

		_tremoloPhase = unchecked(
			(byte)(tremolo.StartPhase + phaseAdvance));

		if (tremolo.Waveform == TrackerWaveform.Random)
		{
			long completedAnchors =
				(long)Math.Floor(elapsedTicks + 1e-9);
			_tremoloRandomAnchorIndex =
				checked(
					tremolo.RandomStartIndex
						+ completedAnchors);
		}
	}

	private static TimeSpan GetTickDuration(double tempo)
	{
		if (!(tempo > 0.0) || double.IsNaN(tempo) || double.IsInfinity(tempo))
			throw new ArgumentOutOfRangeException(nameof(tempo));

		TimeSpan tickDuration = TimeSpan.FromSeconds(
			SequencingConstants.Diachron.TotalSeconds / tempo);
		if (tickDuration <= TimeSpan.Zero)
			throw new InvalidOperationException("Tempo produces a zero-length tracker tick.");

		return tickDuration;
	}

	internal void ObserveOutputFrame(ReadOnlySpan<float> outputFrame)
	{
		if (outputFrame.Length != _lastOutputFrame.Length)
			throw new ArgumentException("Output frame width does not match the voice.", nameof(outputFrame));

		if (_outputHistoryFrames != 0)
			_lastOutputFrame.CopyTo(_previousOutputFrame);

		outputFrame.CopyTo(_lastOutputFrame);
		if (_outputHistoryFrames < 2)
			_outputHistoryFrames++;
	}

	internal void AddCutTo(AntiClickTail tail)
	{
		ArgumentNullException.ThrowIfNull(tail);

		if (_outputHistoryFrames == 0)
			return;

		tail.AddCut(
			_previousOutputFrame,
			_lastOutputFrame,
			havePrevious: _outputHistoryFrames >= 2);
	}
}
