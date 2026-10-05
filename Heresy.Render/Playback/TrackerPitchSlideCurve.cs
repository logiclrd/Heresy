using System;

namespace Heresy.Render.Playback;

using Heresy.Render.Sounds;

/// <summary>
/// Continuous linear-pitch row operator. It preserves the total IT row change
/// of (Speed-1) legacy slide steps but distributes that change over the whole
/// row-time domain.
/// </summary>
public sealed class TrackerPitchSlideCurve : PitchCurve
{
	private readonly double _initialMultiplier;
	private readonly double _linearUnitsPerTick;
	private readonly double _framesPerTick;
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;
	private readonly int _ticksPerRow;

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
		_ticksPerRow = ticksPerRow;
	}

	public TrackerPitchSlideCurve(
		double initialMultiplier,
		double linearUnitsPerTick,
		TrackerTickClock tickClock,
		long startFrame,
		int ticksPerRow)
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
		ArgumentNullException.ThrowIfNull(tickClock);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));

		_initialMultiplier = initialMultiplier;
		_linearUnitsPerTick = linearUnitsPerTick;
		_tickClock = tickClock;
		_startFrame = startFrame;
		_ticksPerRow = ticksPerRow;
	}

	public override double GetMultiplier(long frameOffset)
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
		double legacyEquivalentTicks =
			rowTime
				* Math.Max(0, _ticksPerRow - 1)
				/ _ticksPerRow;

		return _initialMultiplier * Math.Pow(
			2.0,
			_linearUnitsPerTick
				* legacyEquivalentTicks
				/ TrackerVibrato.LinearSlideUnitsPerOctave);
	}
}
