using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Impulse-Tracker-compatible panbrello anchor math. Offsets are expressed in
/// IT's internal 0..256 panning units before projection onto spatial X.
/// </summary>
public static class TrackerPanbrello
{
	public static int GetPanOffsetUnits(
		byte phase,
		byte depth)
	{
		int sample =
			TrackerVibrato.GetFineSineSample(phase);

		// IT adds 2 before signed integer division by 8. C# integer division,
		// like modern IT-compatible players, truncates toward zero.
		return (sample * depth + 2) / 8;
	}

	public static double GetSpatialXOffset(
		byte phase,
		byte depth)
		=> GetPanOffsetUnits(phase, depth) / 128.0;

	public static byte AdvancePhase(
		byte phase,
		byte speed,
		int ticks)
	{
		if (ticks < 0)
			throw new ArgumentOutOfRangeException(nameof(ticks));

		return unchecked(
			(byte)(phase + speed * ticks));
	}
}
