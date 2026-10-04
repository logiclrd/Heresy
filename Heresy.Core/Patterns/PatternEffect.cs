using System;

using Heresy.Core.Sequencing;

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

/// <summary>
/// Tracker Oxx sample offset. The remembered low byte addresses source frames
/// in units of 256.
/// </summary>
public sealed record SampleOffsetPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker SAx high-order sample offset. This sets persistent channel state;
/// it does not seek by itself.
/// </summary>
public sealed record SampleOffsetHighPatternEffect : PatternEffect
{
	public SampleOffsetHighPatternEffect(byte highOffset)
	{
		if (highOffset > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(highOffset));

		HighOffset = highOffset;
	}

	public byte HighOffset { get; }
}

/// <summary>
/// Tracker SCx note cut. IT services x=0 and x=1 on the first post-start tick.
/// </summary>
public sealed record TrackerNoteCutPatternEffect : PatternEffect
{
	public TrackerNoteCutPatternEffect(byte tick)
	{
		if (tick > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(tick));

		Tick = tick;
	}

	public byte Tick { get; }
}

/// <summary>
/// Tracker SDx note delay. The delayed channel event is emitted on the
/// requested post-start tracker tick.
/// </summary>
public sealed record TrackerNoteDelayPatternEffect : PatternEffect
{
	public TrackerNoteDelayPatternEffect(byte tick)
	{
		if (tick > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(tick));

		Tick = tick;
	}

	public byte Tick { get; }
}

/// <summary>
/// Tracker SEx pattern delay. The current row remains active for the requested
/// number of additional row spans without retriggering its note.
/// </summary>
public sealed record TrackerPatternDelayPatternEffect : PatternEffect
{
	public TrackerPatternDelayPatternEffect(byte extraRows)
	{
		if (extraRows > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(extraRows));

		ExtraRows = extraRows;
	}

	public byte ExtraRows { get; }
}

/// <summary>
/// Tracker SBx pattern loop. SB0 marks this channel's loop start and SBx with
/// x&gt;0 repeats from that row x additional times.
/// </summary>
public sealed record TrackerPatternLoopPatternEffect : PatternEffect
{
	public TrackerPatternLoopPatternEffect(byte repeatCount)
	{
		if (repeatCount > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(repeatCount));

		RepeatCount = repeatCount;
	}

	public byte RepeatCount { get; }
}

/// <summary>
/// Tracker S3x vibrato waveform selection. Values 0..3 select sine, ramp-down,
/// square, and random; values 4..15 are accepted but ignored like IT.
/// </summary>
public sealed record TrackerVibratoWaveformPatternEffect : PatternEffect
{
	public TrackerVibratoWaveformPatternEffect(byte value)
	{
		if (value > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Tracker S4x tremolo waveform selection. Values 0..3 select sine, ramp-down,
/// square, and random; values 4..15 are accepted but ignored like IT.
/// </summary>
public sealed record TrackerTremoloWaveformPatternEffect : PatternEffect
{
	public TrackerTremoloWaveformPatternEffect(byte value)
	{
		if (value > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Tracker S6x fine-pattern delay. Extends the current row by individual
/// tracker ticks without starting a new row span.
/// </summary>
public sealed record TrackerFinePatternDelayPatternEffect : PatternEffect
{
	public TrackerFinePatternDelayPatternEffect(byte extraTicks)
	{
		if (extraTicks > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(extraTicks));

		ExtraTicks = extraTicks;
	}

	public byte ExtraTicks { get; }
}

/// <summary>
/// Tracker S1x glissando control. S10 disables glissando; any non-zero low
/// nibble enables semitone-clamped tone portamento.
/// </summary>
public sealed record TrackerGlissandoControlPatternEffect : PatternEffect
{
	public TrackerGlissandoControlPatternEffect(byte value)
	{
		if (value > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Tracker S70/S71/S72 past-note action. Applies cut, note-off, or note-fade
/// to all past/NNA voices originating from this tracker channel.
/// </summary>
public sealed record TrackerPastNoteActionPatternEffect : PatternEffect
{
	public TrackerPastNoteActionPatternEffect(
		TrackerPastNoteAction action)
	{
		if (!Enum.IsDefined(action))
			throw new ArgumentOutOfRangeException(nameof(action));

		Action = action;
	}

	public TrackerPastNoteAction Action { get; }
}
