using System;

using Heresy.Core.Samples;

namespace Heresy.UserInterface.SampleEditing;

public enum SampleLoopBoundary
{
	Start,
	End,
}

public static class SampleLoopWaveformEditing
{
	public static SampleLoop MoveBoundary(
		SampleLoop loop,
		SampleLoopBoundary boundary,
		long requestedFrame,
		long frameCount)
	{
		ArgumentNullException.ThrowIfNull(loop);
		if (frameCount <= 0)
		{
			throw new InvalidOperationException(
				"An active sample loop requires decoded PCM frames.");
		}
		if (loop.Mode == SampleLoopMode.None)
		{
			throw new InvalidOperationException(
				"An inactive sample loop has no editable waveform handles.");
		}

		long normalizedStart =
			Math.Clamp(
				loop.StartFrame,
				0,
				frameCount - 1);
		long normalizedEnd =
			Math.Clamp(
				loop.EndFrameExclusive,
				normalizedStart + 1,
				frameCount);

		return boundary switch
		{
			SampleLoopBoundary.Start =>
				new SampleLoop(
					loop.Mode,
					Math.Clamp(
						requestedFrame,
						0,
						normalizedEnd - 1),
					normalizedEnd),
			SampleLoopBoundary.End =>
				new SampleLoop(
					loop.Mode,
					normalizedStart,
					Math.Clamp(
						requestedFrame,
						normalizedStart + 1,
						frameCount)),
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(boundary)),
		};
	}

	public static bool TryHitBoundary(
		SampleLoop loop,
		long frameCount,
		double width,
		double x,
		double tolerance,
		out SampleLoopBoundary boundary)
	{
		ArgumentNullException.ThrowIfNull(loop);
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (!(width > 0.0)
			|| !double.IsFinite(width))
		{
			throw new ArgumentOutOfRangeException(nameof(width));
		}
		if (tolerance < 0.0
			|| !double.IsFinite(tolerance))
		{
			throw new ArgumentOutOfRangeException(nameof(tolerance));
		}

		boundary = default;
		if (loop.Mode == SampleLoopMode.None
			|| frameCount == 0)
		{
			return false;
		}

		long start =
			Math.Clamp(
				loop.StartFrame,
				0,
				frameCount - 1);
		long end =
			Math.Clamp(
				loop.EndFrameExclusive,
				start + 1,
				frameCount);

		double startX =
			FrameToX(
				start,
				width,
				frameCount);
		double endX =
			FrameToX(
				end,
				width,
				frameCount);
		double startDistance =
			Math.Abs(x - startX);
		double endDistance =
			Math.Abs(x - endX);

		if (startDistance > tolerance
			&& endDistance > tolerance)
		{
			return false;
		}

		boundary =
			startDistance <= endDistance
				? SampleLoopBoundary.Start
				: SampleLoopBoundary.End;
		return true;
	}

	public static double FrameToX(
		long frame,
		double width,
		long frameCount)
	{
		if (!(width >= 0.0)
			|| !double.IsFinite(width))
		{
			throw new ArgumentOutOfRangeException(nameof(width));
		}

		long clamped =
			Math.Clamp(
				frame,
				0,
				frameCount);
		return SampleWaveformEnvelope.FrameToFraction(
				clamped,
				frameCount)
			* width;
	}

	public static long XToFrame(
		double x,
		double width,
		long frameCount)
	{
		if (!(width > 0.0)
			|| !double.IsFinite(width))
		{
			throw new ArgumentOutOfRangeException(nameof(width));
		}
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (!double.IsFinite(x))
			throw new ArgumentOutOfRangeException(nameof(x));

		double fraction =
			Math.Clamp(
				x / width,
				0.0,
				1.0);
		return SampleWaveformEnvelope.FractionToFrame(
			fraction,
			frameCount);
	}
}
