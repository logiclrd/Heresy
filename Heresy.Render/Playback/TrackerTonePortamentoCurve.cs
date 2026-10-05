using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Continuous tone-portamento row operator. It preserves the legacy row-end
/// movement while spreading it over the whole row-time domain, clamps exactly
/// at the target, and optionally quantizes the continuous path for glissando.
/// </summary>
public sealed class TrackerTonePortamentoCurve : PitchCurve
{
	private readonly double _initialMultiplier;
	private readonly double _targetMultiplier;
	private readonly double _linearUnitsPerTick;
	private readonly double _framesPerTick;
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;
	private readonly int _ticksPerRow;
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
		_ticksPerRow = ticksPerRow;
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
		_ticksPerRow = ticksPerRow;
		_direction = Math.Sign(targetMultiplier - initialMultiplier);
		_glissando = glissando;
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double continuous =
			GetContinuousMultiplier(frameOffset);

		if (!_glissando
			|| _direction == 0.0
			|| _linearUnitsPerTick == 0.0)
		{
			return continuous;
		}

		int semitone =
			QuantizeToCrossedSemitone(
				continuous,
				_direction);
		double quantized = Math.Pow(
			2.0,
			semitone / 12.0);

		return _direction > 0.0
			? Math.Min(quantized, _targetMultiplier)
			: Math.Max(quantized, _targetMultiplier);
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
		double rowTime = Math.Clamp(
			rawElapsedTicks,
			0.0,
			_ticksPerRow);
		double elapsedTicks =
			rowTime
				* Math.Max(0, _ticksPerRow - 1)
				/ _ticksPerRow;
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

	private static int QuantizeToCrossedSemitone(
		double multiplier,
		double direction)
	{
		double semitones =
			12.0 * Math.Log2(multiplier);
		double nearest = Math.Round(semitones);

		if (Math.Abs(semitones - nearest) <= 1e-10)
			semitones = nearest;

		return direction >= 0.0
			? checked((int)Math.Floor(semitones))
			: checked((int)Math.Ceiling(semitones));
	}
}
