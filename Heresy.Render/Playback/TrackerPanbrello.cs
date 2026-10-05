using System;

using Heresy.Core.Sequencing;

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
		=> GetPanOffsetUnits(
			TrackerWaveform.Sine,
			phase,
			depth);

	public static int GetPanOffsetUnits(
		TrackerWaveform waveform,
		byte phase,
		byte depth)
	{
		if (waveform == TrackerWaveform.Random)
		{
			throw new ArgumentException(
				"Random panbrello requires a deterministic random anchor.",
				nameof(waveform));
		}

		int sample =
			TrackerVibrato.GetWaveformSample(
				waveform,
				phase);

		return ScaleSampleToPanUnits(
			sample,
			depth);
	}

	public static int GetRandomPanOffsetUnits(
		ulong seed,
		long anchorIndex,
		byte depth)
		=> ScaleSampleToPanUnits(
			TrackerVibrato.GetRandomWaveformSample(
				seed,
				anchorIndex),
			depth);

	public static double GetSpatialXOffset(
		byte phase,
		byte depth)
		=> GetPanOffsetUnits(phase, depth) / 128.0;

	public static double GetSpatialXOffset(
		TrackerWaveform waveform,
		byte phase,
		byte depth)
		=> GetPanOffsetUnits(
			waveform,
			phase,
			depth) / 128.0;

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

	private static int ScaleSampleToPanUnits(
		int sample,
		byte depth)
	{
		// IT adds 2 before signed integer division by 8. C# integer division
		// truncates toward zero, matching the observable tracker behavior.
		return (sample * depth + 2) / 8;
	}
}
