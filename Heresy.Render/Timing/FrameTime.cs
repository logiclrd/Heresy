using System;

namespace Heresy.Render.Timing;

/// <summary>
/// Exact conversion between resolved musical time and output-frame coordinates.
/// Events use ceiling so they are never applied before their requested time.
/// </summary>
public static class FrameTime
{
	public static long Ceiling(TimeSpan time, int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		Int128 numerator = (Int128)time.Ticks * sampleRate;
		Int128 denominator = TimeSpan.TicksPerSecond;
		Int128 quotient = numerator / denominator;
		Int128 remainder = numerator % denominator;

		if (remainder > 0)
			quotient++;

		if (quotient < long.MinValue || quotient > long.MaxValue)
			throw new OverflowException("The requested time is outside the supported frame range.");

		return (long)quotient;
	}

	/// <summary>
	/// Returns the greatest TimeSpan value not later than the mathematical start
	/// time of a non-negative output frame.
	/// </summary>
	public static TimeSpan FrameStartTime(long frame, int sampleRate)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		Int128 ticks = (Int128)frame * TimeSpan.TicksPerSecond / sampleRate;
		if (ticks > long.MaxValue)
			throw new OverflowException("The requested frame is outside the supported TimeSpan range.");

		return TimeSpan.FromTicks((long)ticks);
	}
}
