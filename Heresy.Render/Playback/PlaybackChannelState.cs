using System;
using System.Numerics;

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
		public required Vector3 StartPosition { get; init; }
		public required double SpatialUnitsPerTick { get; init; }
		public required double FramesPerTick { get; init; }
		public required int ActiveTickTransitions { get; init; }
		public required double MinimumX { get; init; }
		public required double MaximumX { get; init; }
	}

	private ActiveSpatialXSlide? _activeSpatialXSlide;

	internal PlaybackChannelState(int outputChannelCount, int sampleRate)
	{
		AntiClickTail = new AntiClickTail(outputChannelCount, sampleRate);
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
		OverallVolume = volume;
		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = volume;
	}

	internal void SetPosition(long absoluteFrame, Vector3 position)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

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

		SynchronizePosition(absoluteFrame);

		double framesPerTick =
			SequencingConstants.Diachron.TotalSeconds
			/ tempo
			* sampleRate;

		if (!(framesPerTick > 0.0)
			|| double.IsNaN(framesPerTick)
			|| double.IsInfinity(framesPerTick))
		{
			throw new InvalidOperationException(
				"Tempo produces an invalid spatial-slide tick duration.");
		}

		_activeSpatialXSlide = new ActiveSpatialXSlide
		{
			StartFrame = absoluteFrame,
			StartPosition = Position,
			SpatialUnitsPerTick = spatialUnitsPerTick,
			FramesPerTick = framesPerTick,
			ActiveTickTransitions = Math.Max(0, ticksPerRow - 1),
			MinimumX = minimumX,
			MaximumX = maximumX,
		};
	}

	internal void ClearSpatialXSlide(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		SynchronizePosition(absoluteFrame);
		_activeSpatialXSlide = null;
	}

	internal void SynchronizePosition(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		ActiveSpatialXSlide? slide = _activeSpatialXSlide;
		if (slide is not null)
		{
			double elapsedTicks = Math.Min(
				Math.Max(
					0.0,
					absoluteFrame - slide.StartFrame)
					/ slide.FramesPerTick,
				slide.ActiveTickTransitions);

			Position = new Vector3(
				(float)Math.Clamp(
					slide.StartPosition.X
						+ slide.SpatialUnitsPerTick
							* elapsedTicks,
					slide.MinimumX,
					slide.MaximumX),
				slide.StartPosition.Y,
				slide.StartPosition.Z);
		}

		if (CurrentVoice is not null)
			CurrentVoice.SoundState.Position = Position;
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
		SynchronizePosition(absoluteFrame);

		CurrentVoice = voice;
		voice.NoteVolume = NoteVolume;
		voice.OverallVolume = OverallVolume;
		voice.SoundState.Position = Position;
	}

	internal void CutCurrentVoice()
	{
		PlaybackVoice? voice = DetachCurrentVoice();
		voice?.AddCutTo(AntiClickTail);
	}
}
