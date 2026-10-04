using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Impulse-Tracker-compatible normal vibrato math for the default linear pitch
/// scale. Phase is a byte-sized index into the original 256-entry fine-sine
/// table; one octave corresponds to 768 linear slide units.
/// </summary>
public static class TrackerVibrato
{
	private static readonly sbyte[] FineSineData =
	[
		  0,  2,  3,  5,  6,  8,  9, 11, 12, 14, 16, 17, 19, 20, 22, 23,
		 24, 26, 27, 29, 30, 32, 33, 34, 36, 37, 38, 39, 41, 42, 43, 44,
		 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 56, 57, 58, 59,
		 59, 60, 60, 61, 61, 62, 62, 62, 63, 63, 63, 64, 64, 64, 64, 64,
		 64, 64, 64, 64, 64, 64, 63, 63, 63, 62, 62, 62, 61, 61, 60, 60,
		 59, 59, 58, 57, 56, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47, 46,
		 45, 44, 43, 42, 41, 39, 38, 37, 36, 34, 33, 32, 30, 29, 27, 26,
		 24, 23, 22, 20, 19, 17, 16, 14, 12, 11,  9,  8,  6,  5,  3,  2,
		  0, -2, -3, -5, -6, -8, -9,-11,-12,-14,-16,-17,-19,-20,-22,-23,
		-24,-26,-27,-29,-30,-32,-33,-34,-36,-37,-38,-39,-41,-42,-43,-44,
		-45,-46,-47,-48,-49,-50,-51,-52,-53,-54,-55,-56,-56,-57,-58,-59,
		-59,-60,-60,-61,-61,-62,-62,-62,-63,-63,-63,-64,-64,-64,-64,-64,
		-64,-64,-64,-64,-64,-64,-63,-63,-63,-62,-62,-62,-61,-61,-60,-60,
		-59,-59,-58,-57,-56,-56,-55,-54,-53,-52,-51,-50,-49,-48,-47,-46,
		-45,-44,-43,-42,-41,-39,-38,-37,-36,-34,-33,-32,-30,-29,-27,-26,
		-24,-23,-22,-20,-19,-17,-16,-14,-12,-11, -9, -8, -6, -5, -3, -2,
	];

	public const double LinearSlideUnitsPerOctave = 768.0;

	public static byte AdvancePhase(byte phase, byte speed)
		=> unchecked((byte)(phase + speed * 4));

	public static int GetLinearSlideUnits(byte phase, byte depth)
	{
		int sine = FineSineData[phase];

		// IT multiplies FineSineData by depth*4, shifts left twice, adds 0x80,
		// then takes signed AH. This is equivalent to the floor below.
		return (int)Math.Floor((sine * depth + 8.0) / 16.0);
	}

	public static double GetPitchMultiplier(byte phase, byte depth)
		=> Math.Pow(
			2.0,
			GetLinearSlideUnits(phase, depth) / LinearSlideUnitsPerOctave);
}
