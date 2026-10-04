using System;

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
		public required TimeSpan TickDuration { get; init; }
		public required TimeSpan NextLegacyTickTime { get; set; }
	}

	private sealed class ActiveNoteVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required double StartVolume { get; init; }
		public required double TrackerUnitsPerTick { get; init; }
		public required double FramesPerTick { get; init; }
		public required int ActiveTickTransitions { get; init; }
	}

	private readonly float[] _previousOutputFrame;
	private readonly float[] _lastOutputFrame;
	private int _outputHistoryFrames;

	private long? _fadeStartFrame;
	private long? _fadeEndFrameExclusive;
	private TimeSpan? _fadeDuration;

	private byte _vibratoPhase;
	private ActiveVibrato? _activeVibrato;
	private ActiveNoteVolumeSlide? _activeNoteVolumeSlide;

	private PitchCurve _basePitchCurve = new ConstantPitchCurve(1.0);
	private long _basePitchCurveStartFrame;
	private double? _tonePortamentoTargetBaseMultiplier;
	private PitchCurve _modulationPitchCurve = new ConstantPitchCurve(1.0);
	private long _modulationPitchCurveStartFrame;

	internal PlaybackVoice(
		ISound sound,
		SoundState soundState,
		NoteConfigurationSnapshot configuration,
		long startFrame,
		int outputChannelCount,
		int sampleRate,
		ResonantFilterParameters filterParameters,
		double noteVolume,
		double overallVolume)
	{
		Sound = sound ?? throw new ArgumentNullException(nameof(sound));
		SoundState = soundState ?? throw new ArgumentNullException(nameof(soundState));
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));

		StartFrame = startFrame;
		NoteVolume = noteVolume;
		OverallVolume = overallVolume;

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

	public double NoteVolume { get; internal set; }

	public double OverallVolume { get; internal set; }

	public bool IsFading => _fadeStartFrame.HasValue;

	public long? FadeStartFrame => _fadeStartFrame;

	public long? FadeEndFrameExclusive => _fadeEndFrameExclusive;

	public TimeSpan? FadeDuration => _fadeDuration;

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
		byte depth)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0) || double.IsNaN(tempo) || double.IsInfinity(tempo))
			throw new ArgumentOutOfRangeException(nameof(tempo));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		CommitVibratoPhaseThrough(eventTime);

		TimeSpan tickDuration = GetTickDuration(tempo);

		_vibratoPhase = TrackerVibrato.AdvancePhase(
			_vibratoPhase,
			speed);

		_activeVibrato = new ActiveVibrato
		{
			Speed = speed,
			TickDuration = tickDuration,
			NextLegacyTickTime = eventTime + tickDuration,
		};

		long relativeFrame = absoluteFrame - StartFrame;
		_modulationPitchCurve = new TrackerVibratoPitchCurve(
			_vibratoPhase,
			speed,
			depth,
			tickDuration,
			sampleRate);
		_modulationPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearPitchModulation(
		long absoluteFrame,
		TimeSpan eventTime)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitVibratoPhaseThrough(eventTime);
		_activeVibrato = null;

		long relativeFrame = absoluteFrame - StartFrame;
		_modulationPitchCurve = new ConstantPitchCurve(1.0);
		_modulationPitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
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

		_basePitchCurve = new TrackerPitchSlideCurve(
			currentBase,
			linearUnitsPerTick,
			GetTickDuration(tempo),
			ticksPerRow,
			sampleRate);
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
		double? targetPitchMultiplier)
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
		double currentBase = GetBasePitchMultiplier(relativeFrame);

		_basePitchCurve = new TrackerTonePortamentoCurve(
			currentBase,
			_tonePortamentoTargetBaseMultiplier.Value,
			linearUnitsPerTick,
			GetTickDuration(tempo),
			ticksPerRow,
			sampleRate);
		_basePitchCurveStartFrame = relativeFrame;

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearTonePortamento(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);

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
			GetNoteVolume(absoluteFrame) + trackerUnits / 64.0,
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

		double current = GetNoteVolume(absoluteFrame);
		NoteVolume = current;

		_activeNoteVolumeSlide = new ActiveNoteVolumeSlide
		{
			StartFrame = absoluteFrame,
			StartVolume = current,
			TrackerUnitsPerTick = trackerUnitsPerTick,
			FramesPerTick = GetTickDuration(tempo).TotalSeconds * sampleRate,
			ActiveTickTransitions = Math.Max(0, ticksPerRow - 1),
		};
	}

	internal double ClearNoteVolumeSlide(long absoluteFrame)
	{
		double current = GetNoteVolume(absoluteFrame);
		NoteVolume = current;
		_activeNoteVolumeSlide = null;
		return current;
	}

	internal double GetNoteVolume(long absoluteFrame)
	{
		ActiveNoteVolumeSlide? slide = _activeNoteVolumeSlide;
		if (slide is null)
			return NoteVolume;

		double elapsedTicks = Math.Min(
			Math.Max(0.0, absoluteFrame - slide.StartFrame)
				/ slide.FramesPerTick,
			slide.ActiveTickTransitions);

		return Math.Clamp(
			slide.StartVolume
				+ slide.TrackerUnitsPerTick * elapsedTicks / 64.0,
			0.0,
			1.0);
	}

	private double GetBasePitchMultiplier(long relativeFrame)
		=> _basePitchCurve.GetMultiplier(
			checked(relativeFrame - _basePitchCurveStartFrame));

	private void RecomposePitchTrajectory(long relativeFrame)
	{
		PitchCurve baseCurve = new OffsetPitchCurve(
			_basePitchCurve,
			checked(relativeFrame - _basePitchCurveStartFrame));
		PitchCurve modulationCurve = new OffsetPitchCurve(
			_modulationPitchCurve,
			checked(relativeFrame - _modulationPitchCurveStartFrame));

		SoundState.PitchTrajectory.SetCurve(
			relativeFrame,
			new ProductPitchCurve(baseCurve, modulationCurve));
	}

	private void CommitVibratoPhaseThrough(TimeSpan eventTime)
	{
		ActiveVibrato? vibrato = _activeVibrato;
		if (vibrato is null)
			return;

		while (vibrato.NextLegacyTickTime < eventTime)
		{
			_vibratoPhase = TrackerVibrato.AdvancePhase(
				_vibratoPhase,
				vibrato.Speed);
			vibrato.NextLegacyTickTime += vibrato.TickDuration;
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
