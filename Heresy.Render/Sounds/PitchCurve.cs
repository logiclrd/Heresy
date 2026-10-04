using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Relative pitch multiplier as a function of output frames elapsed since the
/// start of one trajectory segment.
/// </summary>
public abstract class PitchCurve
{
	public abstract double GetMultiplier(long frameOffset);

	/// <summary>
	/// Sums the per-output-sample multiplier for frames [0, frameCount).
	/// </summary>
	public virtual double GetIntegratedPosition(long frameCount)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		double position = 0.0;
		for (long frame = 0; frame < frameCount; frame++)
			position += GetMultiplier(frame);

		return position;
	}

	/// <summary>
	/// True when projecting arbitrarily far forward is cheap enough for a sound
	/// to use this curve in an end-frame calculation.
	/// </summary>
	public virtual bool CanProjectEndEfficiently => false;
}

public sealed class ConstantPitchCurve : PitchCurve
{
	public ConstantPitchCurve(double multiplier)
	{
		if (!(multiplier > 0.0)
			|| double.IsNaN(multiplier)
			|| double.IsInfinity(multiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(multiplier));
		}

		Multiplier = multiplier;
	}

	public double Multiplier { get; }

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		return Multiplier;
	}

	public override double GetIntegratedPosition(long frameCount)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		return frameCount * Multiplier;
	}

	public override bool CanProjectEndEfficiently => true;
}
