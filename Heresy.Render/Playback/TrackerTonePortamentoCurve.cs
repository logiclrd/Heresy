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
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;
	private readonly int _activeTickTransitions;
	private readonly double _direction;
	private readonly bool _glissando;

	public TrackerTonePortamentoCurve(
		double initialMultiplier,
		double targetMultiplier,
		double linearUnitsPerTick,
		TimeSpan tickDuration,
		int ticksPerRow,
		int sampleRate,
		bool glissando = false)
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
		_glissando = glissando;
	}

	public TrackerTonePortamentoCurve(
		double initialMultiplier,
		double targetMultiplier,
		double linearUnitsPerTick,
		TrackerTickClock tickClock,
		long startFrame,
		int ticksPerRow,
		bool glissando = false)
	{
		if (!(initialMultiplier > 0.0)
			|| double.IsNaN(initialMultiplier)
			|| double.IsInfinity(initialMultiplier))
			throw new ArgumentOutOfRangeException(nameof(initialMultiplier));
		if (!(targetMultiplier > 0.0)
			|| double.IsNaN(targetMultiplier)
			|| double.IsInfinity(targetMultiplier))
			throw new ArgumentOutOfRangeException(nameof(targetMultiplier));
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick)
			|| linearUnitsPerTick < 0.0)
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		ArgumentNullException.ThrowIfNull(tickClock);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));

		_initialMultiplier = initialMultiplier;
		_targetMultiplier = targetMultiplier;
		_linearUnitsPerTick = linearUnitsPerTick;
		_tickClock = tickClock;
		_startFrame = startFrame;
		_activeTickTransitions = Math.Max(0, ticksPerRow - 1);
		_direction = Math.Sign(targetMultiplier - initialMultiplier);
		_glissando = glissando;
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		if (!_glissando)
			return GetContinuousMultiplier(frameOffset);

		if (_direction == 0.0 || _linearUnitsPerTick == 0.0)
			return _initialMultiplier;

		double rawElapsedTicks = _tickClock is null
			? frameOffset / _framesPerTick
			: _tickClock.GetElapsedTicks(
				_startFrame,
				checked(_startFrame + frameOffset));
		double elapsedTicks = Math.Min(
			rawElapsedTicks,
			_activeTickTransitions);
		int tick0 = (int)Math.Floor(elapsedTicks);
		int tick1 = Math.Min(
			tick0 + 1,
			_activeTickTransitions);
		double fraction = elapsedTicks - tick0;

		int semitone0 = QuantizeToNextSemitone(
			GetContinuousMultiplierForTicks(tick0));
		int semitone1 = QuantizeToNextSemitone(
			GetContinuousMultiplierForTicks(tick1));
		int delta = semitone1 - semitone0;

		int semitone = semitone0;
		if (delta != 0)
		{
			int magnitude = Math.Abs(delta);
			int completedSteps = Math.Min(
				magnitude,
				(int)Math.Floor(
					fraction * magnitude + 0.5));
			semitone += Math.Sign(delta) * completedSteps;
		}

		double result = Math.Pow(
			2.0,
			semitone / 12.0);

		return _direction > 0.0
			? Math.Min(result, _targetMultiplier)
			: Math.Max(result, _targetMultiplier);
	}

	public double GetContinuousMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double rawElapsedTicks = _tickClock is null
			? frameOffset / _framesPerTick
			: _tickClock.GetElapsedTicks(
				_startFrame,
				checked(_startFrame + frameOffset));
		double elapsedTicks = Math.Min(
			rawElapsedTicks,
			_activeTickTransitions);
		return GetContinuousMultiplierForTicks(elapsedTicks);
	}

	private double GetContinuousMultiplierForTicks(double elapsedTicks)
	{
		if (_direction == 0.0 || _linearUnitsPerTick == 0.0)
			return _initialMultiplier;

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

	private static int QuantizeToNextSemitone(
		double multiplier)
	{
		double semitones =
			12.0 * Math.Log2(multiplier);
		double nearest = Math.Round(semitones);

		if (Math.Abs(semitones - nearest) <= 1e-10)
			semitones = nearest;

		return checked((int)Math.Ceiling(semitones));
	}
}
