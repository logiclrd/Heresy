using System;

namespace Heresy.Render.Playback;

public static class TrackerRetrigger
{
	public static double ApplyVolumeTransform(
		double volume,
		byte transform)
	{
		if (double.IsNaN(volume)
			|| double.IsInfinity(volume)
			|| volume < 0.0
			|| volume > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}
		if (transform > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(transform));

		int units = Math.Clamp(
			(int)Math.Round(
				volume * 64.0,
				MidpointRounding.AwayFromZero),
			0,
			64);

		units = transform switch
		{
			0x0 => units,
			0x1 => units - 1,
			0x2 => units - 2,
			0x3 => units - 4,
			0x4 => units - 8,
			0x5 => units - 16,
			0x6 => units * 2 / 3,
			0x7 => units / 2,
			0x8 => units,
			0x9 => units + 1,
			0xA => units + 2,
			0xB => units + 4,
			0xC => units + 8,
			0xD => units + 16,
			0xE => units * 3 / 2,
			0xF => units * 2,
			_ => throw new ArgumentOutOfRangeException(nameof(transform)),
		};

		return Math.Clamp(units, 0, 64) / 64.0;
	}
}
