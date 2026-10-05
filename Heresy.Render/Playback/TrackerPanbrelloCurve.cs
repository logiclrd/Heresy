using System;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Playback;

public readonly record struct PanbrelloRandomState(
	byte Position,
	int HeldSample,
	bool HasHeldSample,
	long NextAnchorIndex);

/// <summary>
/// Row-scoped panbrello modulation. Sine, ramp-down and square advance
/// continuously through row time; random panbrello retains its intentionally
/// discrete sample-and-hold behavior.
/// </summary>
public sealed class TrackerPanbrelloCurve
{
	private readonly byte _initialPhase;
	private readonly byte _speed;
	private readonly byte _depth;
	private readonly double _framesPerTick;
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;
	private readonly int _ticksPerRow;
	private readonly TrackerWaveform _waveform;
	private readonly ulong _randomSeed;
	private readonly PanbrelloRandomState _initialRandomState;

	public TrackerPanbrelloCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TimeSpan tickDuration,
		int ticksPerRow,
		int sampleRate,
		TrackerWaveform waveform = TrackerWaveform.Sine,
		ulong randomSeed = 0,
		PanbrelloRandomState initialRandomState = default)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (initialRandomState.NextAnchorIndex < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(initialRandomState));
		}

		double framesPerTick =
			tickDuration.TotalSeconds * sampleRate;
		if (!(framesPerTick > 0.0)
			|| double.IsNaN(framesPerTick)
			|| double.IsInfinity(framesPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		}

		_initialPhase = initialPhase;
		_speed = speed;
		_depth = depth;
		_framesPerTick = framesPerTick;
		_ticksPerRow = ticksPerRow;
		_waveform = waveform;
		_randomSeed = randomSeed;
		_initialRandomState = initialRandomState;
	}

	public TrackerPanbrelloCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TrackerTickClock tickClock,
		long startFrame,
		int ticksPerRow,
		TrackerWaveform waveform = TrackerWaveform.Sine,
		ulong randomSeed = 0,
		PanbrelloRandomState initialRandomState = default)
	{
		ArgumentNullException.ThrowIfNull(tickClock);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (initialRandomState.NextAnchorIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(initialRandomState));

		_initialPhase = initialPhase;
		_speed = speed;
		_depth = depth;
		_ticksPerRow = ticksPerRow;
		_waveform = waveform;
		_randomSeed = randomSeed;
		_initialRandomState = initialRandomState;
		_tickClock = tickClock;
		_startFrame = startFrame;
	}

	public double FramesPerTick => _framesPerTick;

	public TrackerWaveform Waveform => _waveform;

	public double GetSpatialXOffset(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double rawTickPosition = _tickClock is null
			? frameOffset / _framesPerTick
			: _tickClock.GetElapsedTicks(
				_startFrame,
				checked(_startFrame + frameOffset));

		double tickPosition = Math.Clamp(
			rawTickPosition,
			0.0,
			_ticksPerRow);

		int tick0 = (int)Math.Floor(tickPosition);

		if (_waveform == TrackerWaveform.Random)
		{
			PanbrelloRandomState state =
				GetRandomStateAfterTicks(
					Math.Min(
						tick0 + 1,
						_ticksPerRow));

			if (!state.HasHeldSample)
				return 0.0;

			return ScaleRandomSample(state.HeldSample);
		}

		int tick1 = Math.Min(
			tick0 + 1,
			_ticksPerRow);
		double fraction = tickPosition - tick0;

		byte phase0 =
			TrackerPanbrello.AdvancePhase(
				_initialPhase,
				_speed,
				tick0);
		byte phase1 =
			TrackerPanbrello.AdvancePhase(
				_initialPhase,
				_speed,
				tick1);

		double offset0 =
			TrackerPanbrello.GetSpatialXOffset(
				_waveform,
				phase0,
				_depth);
		double offset1 =
			TrackerPanbrello.GetSpatialXOffset(
				_waveform,
				phase1,
				_depth);

		return offset0
			+ (offset1 - offset0) * fraction;
	}

	public PanbrelloRandomState GetRandomStateAfterTicks(
		int processedTicks)
	{
		if (processedTicks < 0
			|| processedTicks > _ticksPerRow)
		{
			throw new ArgumentOutOfRangeException(
				nameof(processedTicks));
		}

		PanbrelloRandomState state =
			_initialRandomState;

		for (int tick = 0; tick < processedTicks; tick++)
		{
			bool chooseNew =
				state.Position == 0
				|| state.Position >= _speed;

			if (chooseNew)
			{
				int sample =
					TrackerVibrato.GetRandomWaveformSample(
						_randomSeed,
						state.NextAnchorIndex);

				state = state with
				{
					Position = 0,
					HeldSample = sample,
					HasHeldSample = true,
					NextAnchorIndex =
						checked(state.NextAnchorIndex + 1),
				};
			}

			state = state with
			{
				Position = unchecked(
					(byte)(state.Position + 1)),
			};
		}

		return state;
	}

	private double ScaleRandomSample(int sample)
	{
		int panUnits =
			(sample * _depth + 2) / 8;
		return panUnits / 128.0;
	}
}
