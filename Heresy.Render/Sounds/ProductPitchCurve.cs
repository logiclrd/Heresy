using System;

namespace Heresy.Render.Sounds;

public sealed class ProductPitchCurve : PitchCurve
{
	public ProductPitchCurve(PitchCurve left, PitchCurve right)
	{
		Left = left ?? throw new ArgumentNullException(nameof(left));
		Right = right ?? throw new ArgumentNullException(nameof(right));
	}

	public PitchCurve Left { get; }
	public PitchCurve Right { get; }

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		return Left.GetMultiplier(frameOffset)
			* Right.GetMultiplier(frameOffset);
	}
}
