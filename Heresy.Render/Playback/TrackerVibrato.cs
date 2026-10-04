using System;

using Heresy.Core.Sequencing;

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

	public static int GetFineSineSample(byte phase)
		=> FineSineData[phase];

	public static int GetWaveformSample(
		TrackerWaveform waveform,
		byte phase)
		=> waveform switch
		{
			TrackerWaveform.Sine => FineSineData[phase],
			TrackerWaveform.RampDown =>
				64 - ((phase + 1) / 2),
			TrackerWaveform.Square =>
				phase < 128 ? 64 : 0,
			TrackerWaveform.Random =>
				throw new ArgumentException(
					"Random tracker waveform samples require a random anchor index.",
					nameof(waveform)),
			_ => throw new ArgumentOutOfRangeException(nameof(waveform)),
		};

	public static int GetRandomWaveformSample(
		ulong seed,
		long anchorIndex)
	{
		if (anchorIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(anchorIndex));

		unchecked
		{
			ulong z =
				seed
				+ 0x9E3779B97F4A7C15UL
					* ((ulong)anchorIndex + 1UL);
			z = (z ^ (z >> 30))
				* 0xBF58476D1CE4E5B9UL;
			z = (z ^ (z >> 27))
				* 0x94D049BB133111EBUL;
			z ^= z >> 31;

			return (int)(z & 0x7FUL) - 64;
		}
	}

	public static double GetContinuousFineSineSample(double phase)
		=> GetContinuousWaveformSample(
			TrackerWaveform.Sine,
			phase);

	public static double GetContinuousWaveformSample(
		TrackerWaveform waveform,
		double phase)
	{
		if (double.IsNaN(phase) || double.IsInfinity(phase))
			throw new ArgumentOutOfRangeException(nameof(phase));
		if (waveform == TrackerWaveform.Random)
		{
			throw new ArgumentException(
				"Random tracker waveforms are interpolated between legacy tick anchors.",
				nameof(waveform));
		}

		double wrapped = WrapPhase(phase);

		if (waveform == TrackerWaveform.Square)
			return wrapped < 128.0 ? 64.0 : 0.0;

		int index0 = (int)Math.Floor(wrapped);
		int index1 = (index0 + 1) & 0xFF;
		double fraction = wrapped - index0;

		double sample0 =
			GetWaveformSample(waveform, (byte)index0);
		double sample1 =
			GetWaveformSample(waveform, (byte)index1);

		return sample0 + (sample1 - sample0) * fraction;
	}

	public static byte AdvancePhase(byte phase, byte speed)
		=> unchecked((byte)(phase + speed * 4));

	public static int GetLinearSlideUnits(byte phase, byte depth)
		=> GetLinearSlideUnits(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static int GetLinearSlideUnits(
		TrackerWaveform waveform,
		byte phase,
		byte depth)
		=> GetLinearSlideUnitsForSample(
			GetWaveformSample(waveform, phase),
			depth);

	public static int GetRandomLinearSlideUnits(
		ulong seed,
		long anchorIndex,
		byte depth)
		=> GetLinearSlideUnitsForSample(
			GetRandomWaveformSample(seed, anchorIndex),
			depth);

	public static double GetContinuousLinearSlideUnits(
		double phase,
		byte depth)
		=> GetContinuousLinearSlideUnits(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static double GetContinuousLinearSlideUnits(
		TrackerWaveform waveform,
		double phase,
		byte depth)
	{
		if (waveform == TrackerWaveform.Random)
		{
			throw new ArgumentException(
				"Random tracker waveforms are interpolated between legacy tick anchors.",
				nameof(waveform));
		}

		double wrapped = WrapPhase(phase);

		if (waveform == TrackerWaveform.Square)
		{
			return GetLinearSlideUnitsForSample(
				wrapped < 128.0 ? 64 : 0,
				depth);
		}

		int index0 = (int)Math.Floor(wrapped);
		int index1 = (index0 + 1) & 0xFF;
		double fraction = wrapped - index0;

		double units0 =
			GetLinearSlideUnits(
				waveform,
				(byte)index0,
				depth);
		double units1 =
			GetLinearSlideUnits(
				waveform,
				(byte)index1,
				depth);

		return units0 + (units1 - units0) * fraction;
	}

	public static double GetPitchMultiplier(byte phase, byte depth)
		=> GetPitchMultiplier(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static double GetPitchMultiplier(
		TrackerWaveform waveform,
		byte phase,
		byte depth)
		=> Math.Pow(
			2.0,
			GetLinearSlideUnits(
				waveform,
				phase,
				depth)
				/ LinearSlideUnitsPerOctave);

	public static double GetContinuousPitchMultiplier(
		double phase,
		byte depth)
		=> GetContinuousPitchMultiplier(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static double GetContinuousPitchMultiplier(
		TrackerWaveform waveform,
		double phase,
		byte depth)
		=> Math.Pow(
			2.0,
			GetContinuousLinearSlideUnits(
				waveform,
				phase,
				depth)
				/ LinearSlideUnitsPerOctave);

	private static int GetLinearSlideUnitsForSample(
		int sample,
		byte depth)
	{
		// IT multiplies the waveform sample by depth*4, shifts left twice,
		// adds 0x80 for rounding, then takes signed AH.
		return (int)Math.Floor(
			(sample * depth + 8.0) / 16.0);
	}

	private static double WrapPhase(double phase)
	{
		if (double.IsNaN(phase) || double.IsInfinity(phase))
			throw new ArgumentOutOfRangeException(nameof(phase));

		double wrapped = phase % 256.0;
		if (wrapped < 0.0)
			wrapped += 256.0;
		return wrapped;
	}
}
