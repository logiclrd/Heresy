using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Envelopes;
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
	private sealed class PitchOperatorBinding
	{
		public required IRowPlaybackOperator Operator { get; init; }
		public required long StartFrame { get; init; }
	}

	private sealed class OperatorPitchCurve : PitchCurve
	{
		private readonly PitchCurve _baseCurve;
		private readonly PitchOperatorBinding[] _operators;
		private readonly TrackerTickClock _tickClock;
		private readonly EnvelopePlaybackState? _pitchEnvelope;
		private readonly long _segmentAbsoluteStartFrame;
		private readonly int _sampleRate;

		public OperatorPitchCurve(
			PitchCurve baseCurve,
			IReadOnlyList<PitchOperatorBinding> operators,
			TrackerTickClock tickClock,
			EnvelopePlaybackState? pitchEnvelope,
			long segmentAbsoluteStartFrame,
			int sampleRate)
		{
			_baseCurve = baseCurve
				?? throw new ArgumentNullException(nameof(baseCurve));
			ArgumentNullException.ThrowIfNull(operators);
			_tickClock = tickClock
				?? throw new ArgumentNullException(nameof(tickClock));
			_pitchEnvelope = pitchEnvelope;
			if (segmentAbsoluteStartFrame < 0)
				throw new ArgumentOutOfRangeException(nameof(segmentAbsoluteStartFrame));
			if (sampleRate <= 0)
				throw new ArgumentOutOfRangeException(nameof(sampleRate));

			_operators = new PitchOperatorBinding[operators.Count];
			for (int i = 0; i < operators.Count; i++)
				_operators[i] = operators[i];

			_segmentAbsoluteStartFrame = segmentAbsoluteStartFrame;
			_sampleRate = sampleRate;
		}

		public override double GetMultiplier(long frameOffset)
		{
			if (frameOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(frameOffset));

			long absoluteFrame = checked(
				_segmentAbsoluteStartFrame + frameOffset);
			double wallTime =
				absoluteFrame / (double)_sampleRate;
			double linearUnits = 0.0;

			foreach (PitchOperatorBinding binding in _operators)
			{
				double rowTime = Math.Max(
					0.0,
					_tickClock.GetElapsedTicks(
						binding.StartFrame,
						Math.Max(
							binding.StartFrame,
							absoluteFrame)));

				binding.Operator.Update(
					wallTime,
					rowTime);
				linearUnits +=
					binding.Operator.Deltas[
						PlaybackParameter.PitchLinearUnits];
			}

			double envelopeOctaves =
				_pitchEnvelope?.GetValue(absoluteFrame) ?? 0.0;
			double multiplier =
				_baseCurve.GetMultiplier(frameOffset)
				* Math.Pow(
					2.0,
					linearUnits
						/ TrackerVibrato.LinearSlideUnitsPerOctave
						+ envelopeOctaves);
			if (!(multiplier > 0.0)
				|| double.IsNaN(multiplier)
				|| double.IsInfinity(multiplier))
			{
				throw new InvalidOperationException(
					"Pitch envelope produced a non-finite pitch multiplier.");
			}
			return multiplier;
		}
	}

	private sealed class ActiveVibrato
	{
		public required byte Speed { get; init; }
		public required TrackerWaveform Waveform { get; init; }
		public required double StartTickPosition { get; init; }
		public required byte StartPhase { get; init; }
		public required long RandomStartIndex { get; init; }
	
		public required FunctionalRowPlaybackOperator Operator { get; init; }
	}

	private sealed class ActiveTremolo
	{
		public required byte Speed { get; init; }
		public required TrackerWaveform Waveform { get; init; }
		public required double StartTickPosition { get; init; }
		public required byte StartPhase { get; init; }
		public required long RandomStartIndex { get; init; }
	
		public required FunctionalRowPlaybackOperator Operator { get; init; }
	}

	private sealed class ActivePitchSlide
	{
		public required long StartFrame { get; init; }
		public required int TicksPerRow { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
	}

	private sealed class ActiveTonePortamento
	{
		public required long StartFrame { get; init; }
		public required int TicksPerRow { get; init; }
		public required TrackerTonePortamentoCurve Curve { get; init; }
		public required FunctionalRowPlaybackOperator Operator { get; init; }
	}

	private sealed class ActiveNoteVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required int TicksPerRow { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
	}

	private readonly float[] _previousOutputFrame;
	private readonly float[] _lastOutputFrame;
	private readonly EnvelopePlaybackState? _volumeEnvelope;
	private readonly EnvelopePlaybackState? _pitchEnvelope;
	private readonly EnvelopePlaybackState? _panningEnvelope;
	private readonly EnvelopePlaybackState? _filterEnvelope;
	private Vector3 _basePosition;
	private ResonantFilterParameters _baseFilterParameters;
	private int _outputHistoryFrames;

	private long? _fadeStartFrame;
	private long? _fadeEndFrameExclusive;
	private TimeSpan? _fadeDuration;
	private bool _noteFadeRequested;
	private NewNoteAction? _newNoteActionOverride;

	private readonly ulong _modulationSeed;
	private readonly TrackerTickClock _tickClock;
	private readonly int _sampleRate;
	private readonly PlaybackOperatorCollection _operators = new();
	private readonly List<PitchOperatorBinding> _pitchOperators = [];

	private byte _vibratoPhase;
	private long _vibratoRandomAnchorIndex = -1;
	private ActiveVibrato? _activeVibrato;

	private byte _tremoloPhase;
	private long _tremoloRandomAnchorIndex = -1;
	private ActiveTremolo? _activeTremolo;
	private TrackerTremoloVolumeCurve? _tremoloVolumeCurve;
	private long _tremoloVolumeCurveStartFrame;

	private ActivePitchSlide? _activePitchSlide;
	private ActiveTonePortamento? _activeTonePortamento;
	private IRowPlaybackOperator? _activeArpeggioOperator;
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
		int originPhysicalChannel,
		double sourceGainMultiplier = 1.0,
		long originPhysicalPlaybackOwner = 0,
		IReadOnlyList<PlaybackChannelState>? enclosingVolumeChannels = null,
		IReadOnlyList<FlattenedSourceVolume>? enclosingSourceVolumes = null)
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
		if (originPhysicalPlaybackOwner < 0)
			throw new ArgumentOutOfRangeException(nameof(originPhysicalPlaybackOwner));
		if (sourceGainMultiplier < 0.0 || !double.IsFinite(sourceGainMultiplier))
			throw new ArgumentOutOfRangeException(nameof(sourceGainMultiplier));

		SourceGainMultiplier = sourceGainMultiplier;
		EnclosingVolumeChannels = enclosingVolumeChannels
			?? Array.Empty<PlaybackChannelState>();
		EnclosingSourceVolumes = enclosingSourceVolumes
			?? Array.Empty<FlattenedSourceVolume>();
		StartFrame = startFrame;
		OriginPhysicalChannel = originPhysicalChannel;
		OriginPhysicalPlaybackOwner = originPhysicalPlaybackOwner;
		NoteVolume = noteVolume;
		OverallVolume = overallVolume;
		_tickClock = tickClock
			?? throw new ArgumentNullException(nameof(tickClock));
		_sampleRate = sampleRate;
		_modulationSeed = modulationSeed;

		_previousOutputFrame = new float[outputChannelCount];
		_lastOutputFrame = new float[outputChannelCount];

		EnvelopeConfigurationSnapshot envelopes = configuration.Envelopes;
		_volumeEnvelope = CreateEnvelopeState(envelopes.Volume, startFrame, sampleRate);
		_pitchEnvelope = CreateEnvelopeState(envelopes.Pitch, startFrame, sampleRate);
		_panningEnvelope = CreateEnvelopeState(envelopes.Panning, startFrame, sampleRate);
		_filterEnvelope = CreateEnvelopeState(envelopes.Filter, startFrame, sampleRate);
		_basePosition = soundState.Position;
		_baseFilterParameters = filterParameters;

		FilterState = new ResonantFilterState(
			outputChannelCount,
			sampleRate,
			filterParameters);

		if (_pitchEnvelope is not null)
			RecomposePitchTrajectory(0);
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
	/// <summary>Logical channel owning this voice, even after NNA migration.</summary>
	public long OriginPhysicalPlaybackOwner { get; }

	/// <summary>The explicit virtual target (if any) where this voice
	/// originated. It survives NNA migration and prevents physical S7x
	/// past-note controls from stealing virtual-channel voices.</summary>
	internal uint? OriginVirtualChannelId { get; init; }
	/// <summary>The cursor owning an invocation-local virtual target.
	/// Null denotes a legacy/global virtual target or physical voice.</summary>
	internal long? OriginScopedVirtualOwner { get; init; }

	/// <summary>Immutable invocation-local source gain, separate from the
	/// mutable shared channel's tracker note volume.</summary>
	public double SourceGainMultiplier { get; }
	/// <summary>Caller-channel overall-volume ancestry remains live while
	/// descendant voices use their own independent logical channel memory.</summary>
	public IReadOnlyList<PlaybackChannelState> EnclosingVolumeChannels { get; }
	/// <summary>Each ancestor invocation contributes its own live note
	/// volume, even when the instigating channel has been reused since.</summary>
	internal IReadOnlyList<FlattenedSourceVolume> EnclosingSourceVolumes { get; }

	public double NoteVolume { get; internal set; }

	public double OverallVolume { get; internal set; }

	/// <summary>
	/// Persistent discrete surround routing captured from the physical channel.
	/// A displaced NNA voice retains this value after migration.
	/// </summary>
	public bool Surround { get; internal set; }

	internal bool HasPanningEnvelope => _panningEnvelope is not null;
	internal bool HasFilterEnvelope => _filterEnvelope is not null;

	public int ActiveOperatorCount => _operators.Count;

	public double BaselinePitchMultiplier
		=> _basePitchCurve is ConstantPitchCurve constant
			? constant.Multiplier
			: 1.0;

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

		_volumeEnvelope?.NoteOff(absoluteFrame);
		_pitchEnvelope?.NoteOff(absoluteFrame);
		_panningEnvelope?.NoteOff(absoluteFrame);
		_filterEnvelope?.NoteOff(absoluteFrame);
	}

	internal long? GetVolumeEnvelopeEndFrameExclusiveAfterNoteOff(
		long absoluteFrame)
		=> _volumeEnvelope?.GetEndFrameExclusiveAfterNoteOff(
			absoluteFrame);

	internal void SetEnvelopeEnabled(
		EnvelopeTarget target,
		long absoluteFrame,
		bool enabled)
	{
		EnvelopePlaybackState? envelope = target switch
		{
			EnvelopeTarget.Volume => _volumeEnvelope,
			EnvelopeTarget.Pitch => _pitchEnvelope,
			EnvelopeTarget.Panning => _panningEnvelope,
			EnvelopeTarget.Filter => _filterEnvelope,
			_ => throw new ArgumentOutOfRangeException(nameof(target)),
		};

		envelope?.SetEnabled(absoluteFrame, enabled);

		if (target == EnvelopeTarget.Pitch && _pitchEnvelope is not null)
		{
			RecomposePitchTrajectory(
				absoluteFrame - StartFrame);
		}
		else if (target == EnvelopeTarget.Panning)
		{
			SynchronizePanningEnvelope(absoluteFrame);
		}
		else if (target == EnvelopeTarget.Filter)
		{
			SynchronizeFilterEnvelope(absoluteFrame);
		}
	}

	internal double GetVolumeEnvelopeValue(long absoluteFrame)
		=> _volumeEnvelope?.GetValue(absoluteFrame) ?? 1.0;

	internal void SetBasePosition(
		long absoluteFrame,
		Vector3 position)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		_basePosition = position;
		SynchronizePanningEnvelope(absoluteFrame);
	}

	internal void SynchronizePanningEnvelope(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (_panningEnvelope is null || Surround)
		{
			SoundState.Position = _basePosition;
			return;
		}

		double baseX = _basePosition.X;
		if (baseX < -1.0 || baseX > 1.0)
		{
			SoundState.Position = _basePosition;
			return;
		}

		double envelope = Math.Clamp(
			_panningEnvelope.GetValue(absoluteFrame),
			-1.0,
			1.0);
		double excursion = 1.0 - Math.Abs(baseX);
		SoundState.Position = new Vector3(
			(float)Math.Clamp(
				baseX + envelope * excursion,
				-1.0,
				1.0),
			_basePosition.Y,
			_basePosition.Z);
	}

	internal void SetBaseFilterParameters(
		long absoluteFrame,
		ResonantFilterParameters parameters)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		_baseFilterParameters = parameters;
		SynchronizeFilterEnvelope(absoluteFrame);
	}

	internal void SynchronizeFilterEnvelope(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (_filterEnvelope is null)
		{
			FilterState.SetParameters(_baseFilterParameters);
			return;
		}

		double cutoff = Math.Clamp(
			_filterEnvelope.GetValue(absoluteFrame),
			0.0,
			1.0);
		FilterState.SetParameters(
			new ResonantFilterParameters(
				cutoff,
				_baseFilterParameters.Resonance));
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
		if (_activeVibrato is not null)
			RemovePitchOperator(_activeVibrato.Operator);

		long randomStartIndex = 0;
		if (waveform == TrackerWaveform.Random)
			randomStartIndex = checked(++_vibratoRandomAnchorIndex);

		TrackerVibratoPitchCurve vibratoCurve = new(
			_vibratoPhase,
			speed,
			depth,
			_tickClock,
			absoluteFrame,
			waveform,
			_modulationSeed ^ 0x5649425241544F52UL,
			randomStartIndex,
			depthScale);

		FunctionalRowPlaybackOperator vibratoOperator = new(
			PlaybackParameter.PitchLinearUnits,
			(wallTimeSeconds, _) =>
			{
				long frame = WallTimeToFrame(wallTimeSeconds);
				double multiplier = vibratoCurve.GetMultiplier(
					Math.Max(0, frame - absoluteFrame));
				return TrackerVibrato.LinearSlideUnitsPerOctave
					* Math.Log2(multiplier);
			},
			commitOnExpire: false);
		AddPitchOperator(vibratoOperator, absoluteFrame);

		_activeVibrato = new ActiveVibrato
		{
			Speed = speed,
			Waveform = waveform,
			StartTickPosition = _tickClock.GetTickPosition(absoluteFrame),
			StartPhase = _vibratoPhase,
			RandomStartIndex = randomStartIndex,
			Operator = vibratoOperator,
		};

		long relativeFrame = absoluteFrame - StartFrame;
		_vibratoPitchCurve = vibratoCurve;
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
		if (_activeVibrato is not null)
			RemovePitchOperator(_activeVibrato.Operator);
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

		if (_activeArpeggioOperator is not null)
			RemovePitchOperator(_activeArpeggioOperator);

		long relativeFrame = absoluteFrame - StartFrame;
		TrackerArpeggioPitchCurve arpeggioCurve = new(
			firstSemitones,
			secondSemitones,
			_tickClock,
			absoluteFrame);
		_arpeggioPitchCurve = arpeggioCurve;
		_arpeggioPitchCurveStartFrame = relativeFrame;

		_activeArpeggioOperator = new FunctionalRowPlaybackOperator(
			PlaybackParameter.PitchLinearUnits,
			(wallTimeSeconds, _) =>
			{
				long frame = WallTimeToFrame(wallTimeSeconds);
				double multiplier = arpeggioCurve.GetMultiplier(
					Math.Max(0, frame - absoluteFrame));
				return TrackerVibrato.LinearSlideUnitsPerOctave
					* Math.Log2(multiplier);
			},
			commitOnExpire: false);
		AddPitchOperator(_activeArpeggioOperator, absoluteFrame);
		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearArpeggio(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		if (_activeArpeggioOperator is not null)
		{
			RemovePitchOperator(_activeArpeggioOperator);
			_activeArpeggioOperator = null;
		}

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
		if (_activeTremolo is not null)
			_operators.Remove(_activeTremolo.Operator);

		long randomStartIndex = 0;
		if (waveform == TrackerWaveform.Random)
			randomStartIndex = checked(++_tremoloRandomAnchorIndex);

		TrackerTremoloVolumeCurve tremoloCurve = new(
			_tremoloPhase,
			speed,
			depth,
			_tickClock,
			absoluteFrame,
			waveform,
			_modulationSeed ^ 0x5452454D4F4C4F00UL,
			randomStartIndex);

		FunctionalRowPlaybackOperator tremoloOperator = new(
			PlaybackParameter.NoteVolume,
			(wallTimeSeconds, _) =>
			{
				long frame = WallTimeToFrame(wallTimeSeconds);
				return tremoloCurve.GetOffsetTrackerUnits(
					Math.Max(0, frame - absoluteFrame)) / 64.0;
			},
			commitOnExpire: false);
		_operators.Add(tremoloOperator);

		_activeTremolo = new ActiveTremolo
		{
			Speed = speed,
			Waveform = waveform,
			StartTickPosition = _tickClock.GetTickPosition(absoluteFrame),
			StartPhase = _tremoloPhase,
			RandomStartIndex = randomStartIndex,
			Operator = tremoloOperator,
		};

		_tremoloVolumeCurve = tremoloCurve;
		_tremoloVolumeCurveStartFrame = absoluteFrame;
	}

	internal void ClearTremolo(
		long absoluteFrame,
		TimeSpan eventTime)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitTremoloPhaseThrough(absoluteFrame);
		if (_activeTremolo is not null)
			_operators.Remove(_activeTremolo.Operator);
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

		if (_activePitchSlide is not null)
		{
			RemovePitchOperator(_activePitchSlide.Operator);
			_activePitchSlide = null;
		}

		_activeTonePortamentoCurve = null;
		_tonePortamentoContinuationBaseMultiplier = null;

		_basePitchCurve = new ConstantPitchCurve(currentBase);
		_basePitchCurveStartFrame = relativeFrame;

		LinearRowPlaybackOperator pitchOperator = new(
			PlaybackParameter.PitchLinearUnits,
			totalDelta:
				linearUnitsPerTick
					* Math.Max(0, ticksPerRow - 1),
			rowSpan: ticksPerRow,
			commitOnExpire: true);
		AddPitchOperator(
			pitchOperator,
			absoluteFrame);
		_activePitchSlide = new ActivePitchSlide
		{
			StartFrame = absoluteFrame,
			TicksPerRow = ticksPerRow,
			Operator = pitchOperator,
		};

		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearPitchSlide(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		long relativeFrame = absoluteFrame - StartFrame;
		double currentBase = GetBasePitchMultiplier(relativeFrame);

		if (_activePitchSlide is not null)
		{
			_operators.Expire(
				_activePitchSlide.Operator,
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					_activePitchSlide.StartFrame,
					_activePitchSlide.TicksPerRow,
					absoluteFrame));
			_pitchOperators.RemoveAll(
				binding => ReferenceEquals(
					binding.Operator,
					_activePitchSlide.Operator));
			_activePitchSlide = null;
		}

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

		CommitTonePortamento(
			absoluteFrame,
			preserveContinuousContinuation: true);

		long relativeFrame = absoluteFrame - StartFrame;
		double audibleBase = GetBasePitchMultiplier(relativeFrame);
		double continuousBase =
			_tonePortamentoContinuationBaseMultiplier
				?? audibleBase;

		TrackerTonePortamentoCurve curve = new(
			continuousBase,
			_tonePortamentoTargetBaseMultiplier.Value,
			linearUnitsPerTick,
			_tickClock,
			absoluteFrame,
			ticksPerRow,
			glissando);

		FunctionalRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.PitchLinearUnits,
			(_, rowTime) =>
			{
				double audible =
					curve.GetMultiplierForRowTime(rowTime);
				return TrackerVibrato.LinearSlideUnitsPerOctave
					* Math.Log2(audible / audibleBase);
			},
			commitOnExpire: true);
		AddPitchOperator(
			playbackOperator,
			absoluteFrame);

		_activeTonePortamento = new ActiveTonePortamento
		{
			StartFrame = absoluteFrame,
			TicksPerRow = ticksPerRow,
			Curve = curve,
			Operator = playbackOperator,
		};

		_activeTonePortamentoCurve = curve;
		_activeTonePortamentoCurveStartFrame = relativeFrame;
		_tonePortamentoContinuationBaseMultiplier = null;

		_basePitchCurve = new ConstantPitchCurve(audibleBase);
		_basePitchCurveStartFrame = relativeFrame;
		RecomposePitchTrajectory(relativeFrame);
	}

	internal void ClearTonePortamento(long absoluteFrame)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitTonePortamento(
			absoluteFrame,
			preserveContinuousContinuation: true);
		RecomposePitchTrajectory(
			absoluteFrame - StartFrame);
	}

	private void CommitTonePortamento(
		long absoluteFrame,
		bool preserveContinuousContinuation)
	{
		ActiveTonePortamento? active =
			_activeTonePortamento;
		if (active is null)
			return;

		long relativeFrame =
			absoluteFrame - StartFrame;
		double rowTime =
			GetOperatorRowTime(
				active.StartFrame,
				active.TicksPerRow,
				absoluteFrame);

		PlaybackParameterDeltas committed =
			_operators.Expire(
				active.Operator,
				GetWallTimeSeconds(absoluteFrame),
				rowTime);
		_pitchOperators.RemoveAll(
			binding => ReferenceEquals(
				binding.Operator,
				active.Operator));

		double baseMultiplier =
			_basePitchCurve.GetMultiplier(
				checked(
					relativeFrame
						- _basePitchCurveStartFrame));
		double committedMultiplier =
			baseMultiplier
			* Math.Pow(
				2.0,
				committed[
					PlaybackParameter.PitchLinearUnits]
					/ TrackerVibrato.LinearSlideUnitsPerOctave);

		_tonePortamentoContinuationBaseMultiplier =
			preserveContinuousContinuation
				? active.Curve.GetContinuousMultiplierForRowTime(
					rowTime)
				: null;

		_basePitchCurve =
			new ConstantPitchCurve(committedMultiplier);
		_basePitchCurveStartFrame = relativeFrame;
		_activeTonePortamento = null;
		_activeTonePortamentoCurve = null;
		_activeTonePortamentoCurveStartFrame = relativeFrame;
	}

	internal void SetNoteVolume(double volume)
	{
		CancelNoteVolumeSlide();
		NoteVolume = volume;
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
			GetBaseNoteVolume(absoluteFrame)
				+ trackerUnits / 64.0,
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

		CommitNoteVolumeSlide(absoluteFrame);

		LinearRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.NoteVolume,
			totalDelta:
				trackerUnitsPerTick
					* Math.Max(0, ticksPerRow - 1)
					/ 64.0,
			rowSpan: ticksPerRow,
			commitOnExpire: true);
		_operators.Add(playbackOperator);

		_activeNoteVolumeSlide =
			new ActiveNoteVolumeSlide
			{
				StartFrame = absoluteFrame,
				TicksPerRow = ticksPerRow,
				Operator = playbackOperator,
			};
	}

	internal double ClearNoteVolumeSlide(long absoluteFrame)
	{
		CommitNoteVolumeSlide(absoluteFrame);
		return NoteVolume;
	}

	private void CommitNoteVolumeSlide(long absoluteFrame)
	{
		ActiveNoteVolumeSlide? slide =
			_activeNoteVolumeSlide;
		if (slide is null)
			return;

		PlaybackParameterDeltas committed =
			_operators.Expire(
				slide.Operator,
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					slide.StartFrame,
					slide.TicksPerRow,
					absoluteFrame));

		NoteVolume = Math.Clamp(
			NoteVolume
				+ committed[PlaybackParameter.NoteVolume],
			0.0,
			1.0);
		_activeNoteVolumeSlide = null;
	}

	private void CancelNoteVolumeSlide()
	{
		if (_activeNoteVolumeSlide is null)
			return;

		_operators.Remove(
			_activeNoteVolumeSlide.Operator);
		_activeNoteVolumeSlide = null;
	}

	internal double GetBaseNoteVolume(long absoluteFrame)
	{
		ActiveNoteVolumeSlide? slide =
			_activeNoteVolumeSlide;
		if (slide is not null)
		{
			slide.Operator.Update(
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					slide.StartFrame,
					slide.TicksPerRow,
					absoluteFrame));
		}

		return Math.Clamp(
			NoteVolume
				+ _operators.GetPersistentTotalDelta(
					PlaybackParameter.NoteVolume),
			0.0,
			1.0);
	}

	public double GetNoteVolume(long absoluteFrame)
	{
		ActiveNoteVolumeSlide? slide =
			_activeNoteVolumeSlide;
		if (slide is not null)
		{
			slide.Operator.Update(
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					slide.StartFrame,
					slide.TicksPerRow,
					absoluteFrame));
		}

		if (_activeTremolo is not null)
		{
			_activeTremolo.Operator.Update(
				GetWallTimeSeconds(absoluteFrame),
				Math.Max(
					0.0,
					_tickClock.GetTickPosition(absoluteFrame)
						- _activeTremolo.StartTickPosition));
		}

		return Math.Clamp(
			NoteVolume
				+ _operators.GetTotalDelta(
					PlaybackParameter.NoteVolume),
			0.0,
			1.0);
	}

	private static EnvelopePlaybackState? CreateEnvelopeState(
		IEnvelopeCurve? curve,
		long startFrame,
		int sampleRate)
		=> curve is null
			? null
			: new EnvelopePlaybackState(
				curve,
				startFrame,
				sampleRate);

	private void AddPitchOperator(
		IRowPlaybackOperator playbackOperator,
		long startFrame)
	{
		_operators.Add(playbackOperator);
		_pitchOperators.Add(
			new PitchOperatorBinding
			{
				Operator = playbackOperator,
				StartFrame = startFrame,
			});
	}

	private void RemovePitchOperator(
		IRowPlaybackOperator playbackOperator)
	{
		_operators.Remove(playbackOperator);
		_pitchOperators.RemoveAll(
			binding => ReferenceEquals(
				binding.Operator,
				playbackOperator));
	}

	private double GetOperatorRowTime(
		long startFrame,
		int ticksPerRow,
		long absoluteFrame)
		=> Math.Clamp(
			_tickClock.GetElapsedTicks(
				startFrame,
				Math.Max(startFrame, absoluteFrame)),
			0.0,
			ticksPerRow);

	private double GetWallTimeSeconds(long absoluteFrame)
		=> absoluteFrame / (double)_sampleRate;

	private long WallTimeToFrame(double wallTimeSeconds)
		=> checked((long)Math.Round(
			wallTimeSeconds * _sampleRate,
			MidpointRounding.AwayFromZero));

	private double GetTonePortamentoContinuousBaseMultiplier(
		long relativeFrame)
	{
		if (_activeTonePortamento is not null)
		{
			long absoluteFrame =
				checked(StartFrame + relativeFrame);
			return _activeTonePortamento.Curve
				.GetContinuousMultiplierForRowTime(
					GetOperatorRowTime(
						_activeTonePortamento.StartFrame,
						_activeTonePortamento.TicksPerRow,
						absoluteFrame));
		}

		if (_tonePortamentoContinuationBaseMultiplier.HasValue)
			return _tonePortamentoContinuationBaseMultiplier.Value;

		return GetBasePitchMultiplier(relativeFrame);
	}

	private double GetBasePitchMultiplier(long relativeFrame)
	{
		long absoluteFrame = checked(StartFrame + relativeFrame);
		double wallTime = GetWallTimeSeconds(absoluteFrame);

		foreach (PitchOperatorBinding binding in _pitchOperators)
		{
			if (!binding.Operator.CommitOnExpire)
				continue;

			binding.Operator.Update(
				wallTime,
				Math.Max(
					0.0,
					_tickClock.GetElapsedTicks(
						binding.StartFrame,
						Math.Max(
							binding.StartFrame,
							absoluteFrame))));
		}

		double linearUnits =
			_operators.GetPersistentTotalDelta(
				PlaybackParameter.PitchLinearUnits);

		return _basePitchCurve.GetMultiplier(
				checked(
					relativeFrame
						- _basePitchCurveStartFrame))
			* Math.Pow(
				2.0,
				linearUnits
					/ TrackerVibrato.LinearSlideUnitsPerOctave);
	}

	private void RecomposePitchTrajectory(long relativeFrame)
	{
		PitchCurve baseCurve = new OffsetPitchCurve(
			_basePitchCurve,
			checked(relativeFrame - _basePitchCurveStartFrame));

		SoundState.PitchTrajectory.SetCurve(
			relativeFrame,
			new OperatorPitchCurve(
				baseCurve,
				_pitchOperators,
				_tickClock,
				_pitchEnvelope,
				checked(StartFrame + relativeFrame),
				_sampleRate));
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
