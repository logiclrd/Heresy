using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Continuous tone-portamento curve calibrated to legacy tracker tick anchors.
/// It moves at a constant rate in IT linear-pitch space, clamps exactly at the
/// target, and holds there for the remainder of the row.
/// </summary>
public sealed class TrackerTonePortamentoCurve : PitchCurve
{
	private readonly double _initialMultiplier;
	private readonly double _targetMultiplier;
	private readonly double _linearUnitsPerTick;
	private readonly double _framesPerTick;
	private readonly int _activeTickTransitions;
	private readonly double _direction;

	public TrackerTonePortamentoCurve(
		double initialMultiplier,
		double targetMultiplier,
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
		if (!(targetMultiplier > 0.0)
			|| double.IsNaN(targetMultiplier)
			|| double.IsInfinity(targetMultiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(targetMultiplier));
		}
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick)
			|| linearUnitsPerTick < 0.0)
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
		_targetMultiplier = targetMultiplier;
		_linearUnitsPerTick = linearUnitsPerTick;
		_framesPerTick = tickDuration.TotalSeconds * sampleRate;
		_activeTickTransitions = Math.Max(0, ticksPerRow - 1);
		_direction = Math.Sign(targetMultiplier - initialMultiplier);
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		if (_direction == 0.0 || _linearUnitsPerTick == 0.0)
			return _initialMultiplier;

		double elapsedTicks = Math.Min(
			frameOffset / _framesPerTick,
			_activeTickTransitions);

		double candidate = _initialMultiplier * Math.Pow(
			2.0,
			_direction
				* _linearUnitsPerTick
				* elapsedTicks
				/ TrackerVibrato.LinearSlideUnitsPerOctave);

		return _direction > 0.0
			? Math.Min(candidate, _targetMultiplier)
			: Math.Max(candidate, _targetMultiplier);
	}
}
