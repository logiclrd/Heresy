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

/// <summary>
/// Continuous pitch slide in IT linear pitch units per tracker tick.
/// Positive values raise pitch and negative values lower it.
/// </summary>
public sealed record PitchSlidePatternEffect : PatternEffect
{
	public PitchSlidePatternEffect(double linearUnitsPerTick)
	{
		if (double.IsNaN(linearUnitsPerTick)
			|| double.IsInfinity(linearUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		}

		LinearUnitsPerTick = linearUnitsPerTick;
	}

	public double LinearUnitsPerTick { get; }
}

/// <summary>
/// Continuous note-volume slide in tracker volume units per tick. Positive
/// values raise volume and negative values lower it.
/// </summary>
public sealed record NoteVolumeSlidePatternEffect : PatternEffect
{
	public NoteVolumeSlidePatternEffect(double trackerUnitsPerTick)
	{
		if (double.IsNaN(trackerUnitsPerTick)
			|| double.IsInfinity(trackerUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerUnitsPerTick));
		}

		TrackerUnitsPerTick = trackerUnitsPerTick;
	}

	public double TrackerUnitsPerTick { get; }
}

/// <summary>Tracker Dxy volume slide, including D00 effect memory.</summary>
public sealed record TrackerVolumeSlidePatternEffect(byte Parameter) : PatternEffect;

/// <summary>Tracker Exx pitch slide down, including shared E/F effect memory.</summary>
public sealed record TrackerPitchSlideDownPatternEffect(byte Parameter) : PatternEffect;

/// <summary>Tracker Fxx pitch slide up, including shared E/F effect memory.</summary>
public sealed record TrackerPitchSlideUpPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Gxx tone portamento. A note in the same cell becomes the target
/// pitch rather than retriggering an already-active voice.
/// </summary>
public sealed record TonePortamentoPatternEffect(byte Parameter) : PatternEffect;

/// <summary>Tracker Jxy arpeggio, including J00 whole-byte memory.</summary>
public sealed record ArpeggioPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Rxy tremolo. High nibble is speed, low nibble depth; zero nibbles
/// independently recall the previous component.
/// </summary>
public sealed record TremoloPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Qxy retrigger. The low nibble is the tick countdown and the high
/// nibble transforms note volume on each retrigger.
/// </summary>
public sealed record RetriggerPatternEffect(byte Parameter) : PatternEffect;
