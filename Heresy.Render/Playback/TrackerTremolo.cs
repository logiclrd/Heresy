using System;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Playback;

/// <summary>
/// Impulse-Tracker-compatible normal tremolo math for the default sine
/// waveform.
/// </summary>
public static class TrackerTremolo
{
	public static int GetVolumeOffsetUnits(byte phase, byte depth)
		=> GetVolumeOffsetUnits(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static int GetVolumeOffsetUnits(
		TrackerWaveform waveform,
		byte phase,
		byte depth)
		=> GetVolumeOffsetUnitsForSample(
			TrackerVibrato.GetWaveformSample(
				waveform,
				phase),
			depth);

	public static int GetRandomVolumeOffsetUnits(
		ulong seed,
		long anchorIndex,
		byte depth)
		=> GetVolumeOffsetUnitsForSample(
			TrackerVibrato.GetRandomWaveformSample(
				seed,
				anchorIndex),
			depth);

	public static double GetContinuousVolumeOffsetUnits(
		double phase,
		byte depth)
		=> GetContinuousVolumeOffsetUnits(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static double GetContinuousVolumeOffsetUnits(
		TrackerWaveform waveform,
		double phase,
		byte depth)
	{
		if (double.IsNaN(phase) || double.IsInfinity(phase))
			throw new ArgumentOutOfRangeException(nameof(phase));
		if (waveform == TrackerWaveform.Random)
		{
			throw new ArgumentException(
				"Random tracker waveforms are interpolated between legacy tick anchors.",
				nameof(waveform));
		}

		double wrapped = phase % 256.0;
		if (wrapped < 0.0)
			wrapped += 256.0;

		if (waveform == TrackerWaveform.Square)
		{
			return GetVolumeOffsetUnitsForSample(
				wrapped < 128.0 ? 64 : 0,
				depth);
		}

		int index0 = (int)Math.Floor(wrapped);
		int index1 = (index0 + 1) & 0xFF;
		double fraction = wrapped - index0;

		double units0 =
			GetVolumeOffsetUnits(
				waveform,
				(byte)index0,
				depth);
		double units1 =
			GetVolumeOffsetUnits(
				waveform,
				(byte)index1,
				depth);

		return units0 + (units1 - units0) * fraction;
	}

	private static int GetVolumeOffsetUnitsForSample(
		int sample,
		byte depth)
	{
		// IT stores depth internally as depth*2, multiplies by the waveform
		// sample, shifts left twice, adds 0x80 for rounding, then takes signed AH.
		return (int)Math.Floor(
			(sample * depth + 16.0) / 32.0);
	}
}
