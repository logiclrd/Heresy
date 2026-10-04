using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Impulse-Tracker-compatible normal tremolo math for the default sine
/// waveform.
/// </summary>
public static class TrackerTremolo
{
	public static int GetVolumeOffsetUnits(byte phase, byte depth)
	{
		int sine = TrackerVibrato.GetFineSineSample(phase);

		// IT stores depth internally as depth*2, multiplies by the fine-sine
		// value, shifts left twice, adds 0x80 for rounding, then takes signed AH.
		return (int)Math.Floor((sine * depth + 16.0) / 32.0);
	}

	public static double GetContinuousVolumeOffsetUnits(
		double phase,
		byte depth)
	{
		if (double.IsNaN(phase) || double.IsInfinity(phase))
			throw new ArgumentOutOfRangeException(nameof(phase));

		double wrapped = phase % 256.0;
		if (wrapped < 0.0)
			wrapped += 256.0;

		int index0 = (int)Math.Floor(wrapped);
		int index1 = (index0 + 1) & 0xFF;
		double fraction = wrapped - index0;

		double units0 = GetVolumeOffsetUnits((byte)index0, depth);
		double units1 = GetVolumeOffsetUnits((byte)index1, depth);

		return units0 + (units1 - units0) * fraction;
	}
}
