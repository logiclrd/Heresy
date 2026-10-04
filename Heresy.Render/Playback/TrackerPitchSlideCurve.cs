using System;

namespace Heresy.Render.Playback;

using Heresy.Render.Sounds;

/// <summary>
/// Continuous linear-pitch slide calibrated to tracker tick anchors. The slide
/// advances for Speed-1 tick intervals, then holds its final value through the
/// final tick interval of the row.
/// </summary>
public sealed class TrackerPitchSlideCurve : PitchCurve
{
	private readonly double _initialMultiplier;
	private readonly double _linearUnitsPerTick;
	private readonly double _framesPerTick;
	private readonly int _activeTickTransitions;

	public TrackerPitchSlideCurve(
		double initialMultiplier,
		double linearUnitsPerTick,
		TimeSpan tickDuration,
		int ticksPerRow,
		int sampleRate)
	{
		if (!(initialMultiplier > 0.0)
			|| double.IsNaN(initialMultiplier)
			|| double.IsInfinity(initialMultiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(initialMultiplier));
		}
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		}
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_initialMultiplier = initialMultiplier;
		_linearUnitsPerTick = linearUnitsPerTick;
		_framesPerTick = tickDuration.TotalSeconds * sampleRate;
		_activeTickTransitions = Math.Max(0, ticksPerRow - 1);
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double elapsedTicks = Math.Min(
			frameOffset / _framesPerTick,
			_activeTickTransitions);

		return _initialMultiplier * Math.Pow(
			2.0,
			_linearUnitsPerTick
				* elapsedTicks
				/ TrackerVibrato.LinearSlideUnitsPerOctave);
	}
}
