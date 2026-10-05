using System;
using System.Numerics;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using Heresy.Render.Filters;
using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Runtime state for one physical playback/tracker channel. Voice-specific
/// state lives in CurrentVoice so it can migrate intact to a virtual voice.
/// </summary>
public sealed class PlaybackChannelState
{
	private sealed class ActiveSpatialXSlide
	{
		public required long StartFrame { get; init; }
		public required double SpatialUnitsPerTick { get; init; }
		public required int TicksPerRow { get; init; }
		public required double MinimumX { get; init; }
		public required double MaximumX { get; init; }
	}

	private sealed class ActiveOverallVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required double TrackerUnitsPerTick { get; init; }
		public required int TicksPerRow { get; init; }
	}

	private sealed class ActiveTremor
	{
		public required byte OnTicks { get; init; }
		public required byte OffTicks { get; init; }
		public required int RemainingRowTicks { get; set; }
		public required double NextTickPosition { get; set; }
	}

	private sealed class ActivePanbrello
	{
		public required long StartFrame { get; init; }
		public required byte InitialPhase { get; init; }
		public required byte Speed { get; init; }
		public required int TicksPerRow { get; init; }
		public required TrackerPanbrelloCurve Curve { get; init; }
	}

	private readonly TrackerTickClock _tickClock;
	private ActiveSpatialXSlide? _activeSpatialXSlide;
	private ActiveOverallVolumeSlide? _activeOverallVolumeSlide;
	private ActiveTremor? _activeTremor;
	private bool _tremorPhaseInitialized;
	private bool _tremorPhaseOn;
	private int _tremorRemainingFutureTicks;
	private ActivePanbrello? _activePanbrello;
	private byte _panbrelloPhase;
	private double _heldPanbrelloOffsetX;
	private readonly ulong _panbrelloRandomSeed;
	private int _panbrelloRandomHeldSample;
	private bool _panbrelloRandomHasHeldSample;
	private long _panbrelloRandomNextAnchorIndex;

	internal PlaybackChannelState(
		int outputChannelCount,
		int sampleRate,
		TrackerTickClock tickClock,
		ulong panbrelloRandomSeed = 0x50414E4252454C4CUL)
	{
		AntiClickTail = new AntiClickTail(outputChannelCount, sampleRate);
		_tickClock = tickClock
			?? throw new ArgumentNullException(nameof(tickClock));
		_panbrelloRandomSeed = panbrelloRandomSeed;
	}

	public PlaybackVoice? CurrentVoice { get; internal set; }

	/// <summary>
	/// Persistent per-note volume used to initialize a newly attached voice and
	/// kept synchronized with the current physical voice.
	/// </summary>
	public double NoteVolume { get; private set; } = 1.0;

	/// <summary>
	/// Persistent overall playback-channel volume.
	/// </summary>
	public double OverallVolume { get; private set; } = 1.0;

	public Vector3 Position { get; private set; } = Vector3.Zero;

	internal bool HasActiveSpatialXSlide =>
		_activeSpatialXSlide is not null;

	internal bool HasActiveContinuousState =>
		_activeSpatialXSlide is not null
		|| _activeOverallVolumeSlide is not null
		|| _activePanbrello is not null;

	internal bool HasActiveTremor =>
		_activeTremor is not null;

	internal double TremorGain =>
		_activeTremor is not null
			&& _tremorPhaseInitialized
			&& !_tremorPhaseOn
			? 0.0
			: 1.0;

	public ResonantFilterParameters FilterParameters { get; private set; } =
		ResonantFilterParameters.Disabled;

	public AntiClickTail AntiClickTail { get; }

	// Convenience accessors retained while callers migrate to CurrentVoice.
	public ISound? CurrentSound => CurrentVoice?.Sound;
	public SoundState? CurrentSoundState => CurrentVoice?.SoundState;
	public long? NoteStartFrame => CurrentVoice?.StartFrame;

	internal void SetNoteVolume(double volume)
	{
		NoteVolume = volume;
		CurrentVoice?.SetNoteVolume(volume);
	}

	internal void CaptureCurrentNoteVolume(double volume)
		=> NoteVolume = volume;

	internal void SetOverallVolume(double volume)
	{
		_activeOverallVolumeSlide = null;
		OverallVolume = volume;
		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = volume;
	}

	internal void SetOverallVolume(
		long absoluteFrame,
		double volume)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		SetOverallVolume(volume);
	}

	internal void AdjustOverallVolume(
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

		_activeOverallVolumeSlide = null;

		OverallVolume = Math.Clamp(
			OverallVolume + trackerUnits / 64.0,
			0.0,
			1.0);

		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = OverallVolume;
	}

	internal void SetOverallVolumeSlide(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		double trackerUnitsPerTick)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(nameof(tempo));
		}
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (double.IsNaN(trackerUnitsPerTick)
			|| double.IsInfinity(trackerUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerUnitsPerTick));
		}

		if (_activeOverallVolumeSlide is not null)
		{
			OverallVolume =
				GetEffectiveOverallVolume(absoluteFrame);
		}

		_activeOverallVolumeSlide = new ActiveOverallVolumeSlide
		{
			StartFrame = absoluteFrame,
			TrackerUnitsPerTick = trackerUnitsPerTick,
			TicksPerRow = ticksPerRow,
		};
	}

	internal void ClearOverallVolumeSlide(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (_activeOverallVolumeSlide is not null)
		{
			OverallVolume =
				GetEffectiveOverallVolume(absoluteFrame);
		}
		_activeOverallVolumeSlide = null;

		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = OverallVolume;
	}

	internal void SynchronizeOverallVolume(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (CurrentVoice is not null)
		{
			CurrentVoice.OverallVolume =
				GetEffectiveOverallVolume(absoluteFrame);
		}
	}

	private double GetEffectiveOverallVolume(long absoluteFrame)
	{
		ActiveOverallVolumeSlide? slide =
			_activeOverallVolumeSlide;
		if (slide is null)
			return OverallVolume;

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
			OverallVolume
				+ slide.TrackerUnitsPerTick
					* legacyEquivalentTicks / 64.0,
			0.0,
			1.0);
	}

	internal void SetPosition(long absoluteFrame, Vector3 position)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CancelPanbrello(absoluteFrame);
		_activeSpatialXSlide = null;
		Position = position;
		if (CurrentVoice is not null)
			CurrentVoice.SoundState.Position = position;
	}

	internal void AdjustSpatialX(
		long absoluteFrame,
		double deltaX,
		double minimumX,
		double maximumX)
	{
		ValidateSpatialSlide(
			absoluteFrame,
			deltaX,
			minimumX,
			maximumX);

		CancelPanbrello(absoluteFrame);
		SynchronizePosition(absoluteFrame);
		_activeSpatialXSlide = null;

		Position = new Vector3(
			(float)Math.Clamp(
				Position.X + deltaX,
				minimumX,
				maximumX),
			Position.Y,
			Position.Z);

		if (CurrentVoice is not null)
			CurrentVoice.SoundState.Position = Position;
	}

	internal void SetSpatialXSlide(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		double spatialUnitsPerTick,
		double minimumX,
		double maximumX)
	{
		ValidateSpatialSlide(
			absoluteFrame,
			spatialUnitsPerTick,
			minimumX,
			maximumX);
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(nameof(tempo));
		}
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		CancelPanbrello(absoluteFrame);

		if (_activeSpatialXSlide is not null)
			CommitSpatialXSlide(absoluteFrame);

		_activeSpatialXSlide = new ActiveSpatialXSlide
		{
			StartFrame = absoluteFrame,
			SpatialUnitsPerTick = spatialUnitsPerTick,
			TicksPerRow = ticksPerRow,
			MinimumX = minimumX,
			MaximumX = maximumX,
		};
	}

	internal void ClearSpatialXSlide(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CommitSpatialXSlide(absoluteFrame);
		_activeSpatialXSlide = null;
		SynchronizePosition(absoluteFrame);
	}

	private void CommitSpatialXSlide(long absoluteFrame)
	{
		ActiveSpatialXSlide? slide = _activeSpatialXSlide;
		if (slide is null)
			return;

		double deltaX =
			GetSpatialXSlideDelta(absoluteFrame, slide);

		Position = new Vector3(
			(float)Math.Clamp(
				Position.X + deltaX,
				slide.MinimumX,
				slide.MaximumX),
			Position.Y,
			Position.Z);
	}

	private double GetSpatialXSlideDelta(
		long absoluteFrame,
		ActiveSpatialXSlide slide)
	{
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

		return slide.SpatialUnitsPerTick
			* legacyEquivalentTicks;
	}

	internal void SynchronizePosition(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (CurrentVoice is not null)
		{
			CurrentVoice.SoundState.Position =
				GetEffectivePosition(absoluteFrame);
		}
	}

	internal void SynchronizeContinuousState(long absoluteFrame)
	{
		SynchronizePosition(absoluteFrame);
		SynchronizeOverallVolume(absoluteFrame);
	}

	internal void SetPanbrelloWaveform(
		long absoluteFrame,
		TrackerWaveform waveform)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));

		if (_activePanbrello is not null)
		{
			CommitPanbrelloThrough(
				absoluteFrame,
				retainOffset: true);
		}

		_panbrelloPhase = 0;
		SynchronizePosition(absoluteFrame);
	}

	internal void SetPanbrello(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		byte speed,
		byte depth,
		TrackerWaveform waveform)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(nameof(tempo));
		}
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));

		if (_activePanbrello is not null)
			CommitPanbrelloThrough(absoluteFrame, retainOffset: true);

		_activePanbrello = new ActivePanbrello
		{
			StartFrame = absoluteFrame,
			InitialPhase = _panbrelloPhase,
			Speed = speed,
			TicksPerRow = ticksPerRow,
			Curve = new TrackerPanbrelloCurve(
				_panbrelloPhase,
				speed,
				depth,
				_tickClock,
				absoluteFrame,
				ticksPerRow,
				waveform,
				_panbrelloRandomSeed,
				new PanbrelloRandomState(
					_panbrelloPhase,
					_panbrelloRandomHeldSample,
					_panbrelloRandomHasHeldSample,
					_panbrelloRandomNextAnchorIndex)),
		};

		SynchronizePosition(absoluteFrame);
	}

	internal void ClearPanbrello(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CommitPanbrelloThrough(
			absoluteFrame,
			retainOffset: false);
		SynchronizePosition(absoluteFrame);
	}

	internal void ResetPanbrelloOffsetForNewNote()
		=> _heldPanbrelloOffsetX = 0.0;

	private void CancelPanbrello(long absoluteFrame)
	{
		CommitPanbrelloThrough(
			absoluteFrame,
			retainOffset: false);
	}

	private void CommitPanbrelloThrough(
		long absoluteFrame,
		bool retainOffset)
	{
		ActivePanbrello? active = _activePanbrello;
		if (active is null)
		{
			if (!retainOffset)
				_heldPanbrelloOffsetX = 0.0;
			return;
		}

		long frameOffset = Math.Max(
			0,
			absoluteFrame - active.StartFrame);

		if (retainOffset)
		{
			_heldPanbrelloOffsetX =
				active.Curve.GetSpatialXOffset(frameOffset);
		}
		else
		{
			_heldPanbrelloOffsetX = 0.0;
		}

		double elapsedTicks =
			_tickClock.GetElapsedTicks(
				active.StartFrame,
				absoluteFrame);
		int processedTicks = Math.Min(
			active.TicksPerRow,
			Math.Max(
				0,
				(int)Math.Floor(elapsedTicks + 1e-9)));

		if (active.Curve.Waveform == TrackerWaveform.Random)
		{
			PanbrelloRandomState randomState =
				active.Curve.GetRandomStateAfterTicks(
					processedTicks);

			_panbrelloPhase = randomState.Position;
			_panbrelloRandomHeldSample =
				randomState.HeldSample;
			_panbrelloRandomHasHeldSample =
				randomState.HasHeldSample;
			_panbrelloRandomNextAnchorIndex =
				randomState.NextAnchorIndex;
		}
		else
		{
			_panbrelloPhase =
				TrackerPanbrello.AdvancePhase(
					active.InitialPhase,
					active.Speed,
					processedTicks);
		}

		_activePanbrello = null;
	}

	private Vector3 GetEffectivePosition(long absoluteFrame)
	{
		double offsetX = _heldPanbrelloOffsetX;
		ActivePanbrello? panbrello = _activePanbrello;
		if (panbrello is not null)
		{
			long frameOffset = Math.Max(
				0,
				absoluteFrame - panbrello.StartFrame);
			offsetX =
				panbrello.Curve.GetSpatialXOffset(frameOffset);
		}

		ActiveSpatialXSlide? slide = _activeSpatialXSlide;
		double slideX = slide is null
			? 0.0
			: GetSpatialXSlideDelta(
				absoluteFrame,
				slide);

		double minimumX = slide?.MinimumX ?? -1.0;
		double maximumX = slide?.MaximumX ?? 1.0;

		return new Vector3(
			(float)Math.Clamp(
				Position.X + slideX + offsetX,
				minimumX,
				maximumX),
			Position.Y,
			Position.Z);
	}

	internal void SetTremor(
		long absoluteFrame,
		double tempo,
		int ticksPerRow,
		int sampleRate,
		byte onTicks,
		byte offTicks)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(nameof(tempo));
		}
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (onTicks == 0)
			throw new ArgumentOutOfRangeException(nameof(onTicks));
		if (offTicks == 0)
			throw new ArgumentOutOfRangeException(nameof(offTicks));

		_activeTremor = new ActiveTremor
		{
			OnTicks = onTicks,
			OffTicks = offTicks,
			RemainingRowTicks = ticksPerRow,
			NextTickPosition =
				_tickClock.GetTickPosition(absoluteFrame),
		};
	}

	internal void ClearTremor()
		=> _activeTremor = null;

	internal void SynchronizeTremor(
		long absoluteFrame,
		bool hasCurrentVoice)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		ActiveTremor? tremor = _activeTremor;
		if (tremor is null)
			return;

		double tickPosition =
			_tickClock.GetTickPosition(absoluteFrame);

		while (tremor.RemainingRowTicks > 0
			&& tremor.NextTickPosition
				<= tickPosition + 1e-9)
		{
			if (hasCurrentVoice)
				AdvanceTremorTick(tremor);

			tremor.RemainingRowTicks--;
			tremor.NextTickPosition += 1.0;
		}
	}

	private void AdvanceTremorTick(ActiveTremor tremor)
	{
		if (!_tremorPhaseInitialized)
		{
			_tremorPhaseInitialized = true;
			_tremorPhaseOn = true;
			_tremorRemainingFutureTicks =
				tremor.OnTicks - 1;
			return;
		}

		if (_tremorRemainingFutureTicks > 0)
		{
			_tremorRemainingFutureTicks--;
			return;
		}

		_tremorPhaseOn = !_tremorPhaseOn;
		_tremorRemainingFutureTicks =
			(_tremorPhaseOn
				? tremor.OnTicks
				: tremor.OffTicks)
			- 1;
	}

	private static void ValidateSpatialSlide(
		long absoluteFrame,
		double value,
		double minimumX,
		double maximumX)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(nameof(value));
		if (double.IsNaN(minimumX)
			|| double.IsInfinity(minimumX)
			|| double.IsNaN(maximumX)
			|| double.IsInfinity(maximumX)
			|| minimumX > maximumX)
		{
			throw new ArgumentOutOfRangeException(nameof(minimumX));
		}
	}

	internal void SetFilterParameters(ResonantFilterParameters parameters)
	{
		FilterParameters = parameters;
		CurrentVoice?.FilterState.SetParameters(parameters);
	}

	internal PlaybackVoice? DetachCurrentVoice()
	{
		PlaybackVoice? voice = CurrentVoice;
		CurrentVoice = null;
		return voice;
	}

	internal void AttachVoice(
		PlaybackVoice voice,
		long absoluteFrame)
	{
		SynchronizeContinuousState(absoluteFrame);

		CurrentVoice = voice;
		voice.NoteVolume = NoteVolume;
		voice.OverallVolume = OverallVolume;
		voice.SoundState.Position =
			GetEffectivePosition(absoluteFrame);
	}

	internal void CutCurrentVoice()
	{
		PlaybackVoice? voice = DetachCurrentVoice();
		voice?.AddCutTo(AntiClickTail);
	}
}
