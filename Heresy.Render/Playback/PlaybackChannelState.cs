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
		public required int TicksPerRow { get; init; }
		public required double MinimumX { get; init; }
		public required double MaximumX { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
	}

	private sealed class ActiveOverallVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required int TicksPerRow { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
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
		public required FunctionalRowPlaybackOperator Operator { get; init; }
	}

	private readonly TrackerTickClock _tickClock;
	private readonly int _sampleRate;
	private readonly PlaybackOperatorCollection _operators = new();
	private ActiveSpatialXSlide? _activeSpatialXSlide;
	private ActiveOverallVolumeSlide? _activeOverallVolumeSlide;
	private ActiveTremor? _activeTremor;
	private bool _tremorPhaseInitialized;
	private bool _tremorPhaseOn;
	private int _tremorRemainingFutureTicks;
	private ActivePanbrello? _activePanbrello;
	private byte _panbrelloPhase;
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
		_sampleRate = sampleRate;
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

	public int ActiveOperatorCount => _operators.Count;

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
		CancelOverallVolumeSlide();
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

		CancelOverallVolumeSlide();

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

		CommitOverallVolumeSlide(absoluteFrame);

		LinearRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.OverallVolume,
			totalDelta:
				trackerUnitsPerTick
					* Math.Max(0, ticksPerRow - 1)
					/ 64.0,
			rowSpan: ticksPerRow,
			commitOnExpire: true);
		_operators.Add(playbackOperator);

		_activeOverallVolumeSlide =
			new ActiveOverallVolumeSlide
			{
				StartFrame = absoluteFrame,
				TicksPerRow = ticksPerRow,
				Operator = playbackOperator,
			};
	}

	internal void ClearOverallVolumeSlide(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CommitOverallVolumeSlide(absoluteFrame);

		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = OverallVolume;
	}

	private void CommitOverallVolumeSlide(long absoluteFrame)
	{
		ActiveOverallVolumeSlide? slide =
			_activeOverallVolumeSlide;
		if (slide is null)
			return;

		double rowTime =
			GetOperatorRowTime(
				slide.StartFrame,
				slide.TicksPerRow,
				absoluteFrame);

		PlaybackParameterDeltas committed =
			_operators.Expire(
				slide.Operator,
				GetWallTimeSeconds(absoluteFrame),
				rowTime);

		OverallVolume = Math.Clamp(
			OverallVolume
				+ committed[
					PlaybackParameter.OverallVolume],
			0.0,
			1.0);
		_activeOverallVolumeSlide = null;
	}

	private void CancelOverallVolumeSlide()
	{
		if (_activeOverallVolumeSlide is null)
			return;

		_operators.Remove(
			_activeOverallVolumeSlide.Operator);
		_activeOverallVolumeSlide = null;
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
			OverallVolume
				+ _operators.GetTotalDelta(
					PlaybackParameter.OverallVolume),
			0.0,
			1.0);
	}

	internal void SetPosition(long absoluteFrame, Vector3 position)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CancelPanbrello(absoluteFrame);
		CancelSpatialXSlide();
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
		CancelSpatialXSlide();

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
		CommitSpatialXSlide(absoluteFrame);

		LinearRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.SpatialX,
			totalDelta:
				spatialUnitsPerTick
					* Math.Max(0, ticksPerRow - 1),
			rowSpan: ticksPerRow,
			commitOnExpire: true);
		_operators.Add(playbackOperator);

		_activeSpatialXSlide =
			new ActiveSpatialXSlide
			{
				StartFrame = absoluteFrame,
				TicksPerRow = ticksPerRow,
				MinimumX = minimumX,
				MaximumX = maximumX,
				Operator = playbackOperator,
			};
	}

	internal void ClearSpatialXSlide(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CommitSpatialXSlide(absoluteFrame);
		SynchronizePosition(absoluteFrame);
	}

	private void CommitSpatialXSlide(long absoluteFrame)
	{
		ActiveSpatialXSlide? slide =
			_activeSpatialXSlide;
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

		Position = new Vector3(
			(float)Math.Clamp(
				Position.X
					+ committed[PlaybackParameter.SpatialX],
				slide.MinimumX,
				slide.MaximumX),
			Position.Y,
			Position.Z);
		_activeSpatialXSlide = null;
	}


	private void CancelSpatialXSlide()
	{
		if (_activeSpatialXSlide is null)
			return;

		_operators.Remove(
			_activeSpatialXSlide.Operator);
		_activeSpatialXSlide = null;
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

		ExpirePanbrello(absoluteFrame);
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

		ExpirePanbrello(absoluteFrame);

		TrackerPanbrelloCurve curve =
			new TrackerPanbrelloCurve(
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
					_panbrelloRandomNextAnchorIndex));

		FunctionalRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.SpatialX,
			(_, rowTime) =>
				curve.GetSpatialXOffsetForRowTime(
					rowTime),
			commitOnExpire: false);
		_operators.Add(playbackOperator);

		_activePanbrello = new ActivePanbrello
		{
			StartFrame = absoluteFrame,
			InitialPhase = _panbrelloPhase,
			Speed = speed,
			TicksPerRow = ticksPerRow,
			Curve = curve,
			Operator = playbackOperator,
		};

		SynchronizePosition(absoluteFrame);
	}

	internal void ClearPanbrello(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		ExpirePanbrello(absoluteFrame);
		SynchronizePosition(absoluteFrame);
	}

	internal void ResetPanbrelloOffsetForNewNote()
	{
		// Panbrello belongs to the physical channel for the rest of its row.
		// A displaced voice keeps the effective position it captured, while the
		// new physical voice continues to receive the active operator.
	}

	private void CancelPanbrello(long absoluteFrame)
		=> ExpirePanbrello(absoluteFrame);

	private void ExpirePanbrello(long absoluteFrame)
	{
		ActivePanbrello? active = _activePanbrello;
		if (active is null)
			return;

		double rowTime =
			GetOperatorRowTime(
				active.StartFrame,
				active.TicksPerRow,
				absoluteFrame);

		_operators.Expire(
			active.Operator,
			GetWallTimeSeconds(absoluteFrame),
			rowTime);

		int processedTicks = Math.Min(
			active.TicksPerRow,
			Math.Max(
				0,
				(int)Math.Floor(rowTime + 1e-9)));

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
		ActiveSpatialXSlide? slide =
			_activeSpatialXSlide;
		if (slide is not null)
		{
			slide.Operator.Update(
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					slide.StartFrame,
					slide.TicksPerRow,
					absoluteFrame));
		}

		ActivePanbrello? panbrello =
			_activePanbrello;
		if (panbrello is not null)
		{
			panbrello.Operator.Update(
				GetWallTimeSeconds(absoluteFrame),
				GetOperatorRowTime(
					panbrello.StartFrame,
					panbrello.TicksPerRow,
					absoluteFrame));
		}

		double minimumX = slide?.MinimumX ?? -1.0;
		double maximumX = slide?.MaximumX ?? 1.0;
		double operatorX =
			_operators.GetTotalDelta(
				PlaybackParameter.SpatialX);

		return new Vector3(
			(float)Math.Clamp(
				Position.X + operatorX,
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
