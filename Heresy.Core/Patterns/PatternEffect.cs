using System;

namespace Heresy.Core.Patterns;

/// <summary>
/// Semantic effect stored by the data-driven pattern grid. Familiar tracker
/// effect notation and effect-memory behavior will be layered on top of these
/// values rather than becoming part of the core pattern representation.
/// </summary>
public abstract record PatternEffect;

public sealed record SetTempoPatternEffect : PatternEffect
{
	public SetTempoPatternEffect(double ticksPerDiachron)
	{
		if (!(ticksPerDiachron > 0.0) || double.IsNaN(ticksPerDiachron) || double.IsInfinity(ticksPerDiachron))
			throw new ArgumentOutOfRangeException(nameof(ticksPerDiachron));

		TicksPerDiachron = ticksPerDiachron;
	}

	public double TicksPerDiachron { get; }
}

public sealed record SetSpeedPatternEffect : PatternEffect
{
	public SetSpeedPatternEffect(int ticksPerRow)
	{
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));

		TicksPerRow = ticksPerRow;
	}

	public int TicksPerRow { get; }
}

public sealed record SetNoteVolumePatternEffect(double Volume) : PatternEffect;

public sealed record SetOverallChannelVolumePatternEffect(double Volume) : PatternEffect;

public sealed record SetPlaybackFrequencyPatternEffect : PatternEffect
{
	public SetPlaybackFrequencyPatternEffect(double frequency)
	{
		if (!(frequency > 0.0) || double.IsNaN(frequency) || double.IsInfinity(frequency))
			throw new ArgumentOutOfRangeException(nameof(frequency));

		Frequency = frequency;
	}

	public double Frequency { get; }
}

public sealed record SetPlaybackOffsetPatternEffect : PatternEffect
{
	public SetPlaybackOffsetPatternEffect(TimeSpan offset)
	{
		if (offset < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(offset));

		Offset = offset;
	}

	public TimeSpan Offset { get; }
}

/// <summary>
/// Normal tracker-style vibrato (IT/S3M Hxy semantics). The high nibble is
/// speed and the low nibble is depth; zero nibbles recall channel memory.
/// </summary>
public sealed record VibratoPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Sets normalized Impulse-Tracker-style resonant low-pass filter parameters.
/// </summary>
public sealed record SetResonantFilterPatternEffect : PatternEffect
{
	public SetResonantFilterPatternEffect(double cutoff, double resonance)
	{
		if (double.IsNaN(cutoff)
			|| double.IsInfinity(cutoff)
			|| cutoff < 0.0
			|| cutoff > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(cutoff));
		}

		if (double.IsNaN(resonance)
			|| double.IsInfinity(resonance)
			|| resonance < 0.0
			|| resonance > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(resonance));
		}

		Cutoff = cutoff;
		Resonance = resonance;
	}

	public double Cutoff { get; }
	public double Resonance { get; }
}
