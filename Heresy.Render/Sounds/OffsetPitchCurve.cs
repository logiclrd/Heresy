using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Presents an existing pitch curve starting from a later point in that curve.
/// </summary>
public sealed class OffsetPitchCurve : PitchCurve
{
	public OffsetPitchCurve(PitchCurve inner, long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		Inner = inner ?? throw new ArgumentNullException(nameof(inner));
		FrameOffset = frameOffset;
	}

	public PitchCurve Inner { get; }
	public long FrameOffset { get; }

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		return Inner.GetMultiplier(checked(FrameOffset + frameOffset));
	}
}
