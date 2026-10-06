using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Patterns;

/// <summary>
/// Semantic effect stored by the data-driven pattern grid. Familiar tracker
/// effect notation and effect-memory behavior will be layered on top of these
/// values rather than becoming part of the core pattern representation.
/// </summary>
public abstract record PatternEffect;

/// <summary>
/// Persisted editor slot for a newly inserted IT-style effect whose command
/// has not been chosen yet. It is musically inert until replaced by a concrete
/// tracker effect; the byte is retained so parameter-first editing can still
/// be represented without inventing playback semantics.
/// </summary>
public sealed record EmptyTrackerPatternEffect(
	byte Parameter = 0) : PatternEffect;

/// <summary>
/// Tracker Bxx order jump. The byte addresses a sequence order directly.
/// </summary>
public sealed record TrackerOrderJumpPatternEffect(byte Order) : PatternEffect;

/// <summary>
/// Tracker Cxx pattern break. The byte is the row at which the destination
/// sequence entry begins.
/// </summary>
public sealed record TrackerPatternBreakPatternEffect(byte Row) : PatternEffect;

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

/// <summary>
/// Tracker Txx tempo command. T20..TFF set tempo on tick zero; T0x/T1x
/// slide tempo on subsequent ticks. T00 recalls whole-byte channel memory.
/// </summary>
public sealed record TrackerTempoPatternEffect(byte Parameter) : PatternEffect;
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
/// Tracker SFx MIDI-macro selection. The low nibble selects one of the 16
/// parameterized macros subsequently invoked by Z00..Z7F on this channel.
/// </summary>
public sealed record TrackerMidiMacroSelectPatternEffect : PatternEffect
{
	public TrackerMidiMacroSelectPatternEffect(byte macro)
	{
		if (macro > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(macro));
		Macro = macro;
	}

	public byte Macro { get; }
}

/// <summary>
/// Tracker Zxx MIDI-macro invocation. Z00..Z7F invoke the selected SFx macro
/// with xx as macro data; Z80..ZFF invoke one of 128 fixed macros directly.
/// </summary>
public sealed record TrackerMidiMacroPatternEffect(byte Parameter) : PatternEffect;

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

/// <summary>
/// One IT volume-column A-H compatibility operation. The parameter is the
/// displayed decimal digit 0..9; memory/scaling is resolved by the common
/// pattern processor because it differs from same-letter effect-column commands.
/// </summary>
public sealed record TrackerVolumeColumnPatternEffect : PatternEffect
{
	public TrackerVolumeColumnPatternEffect(
		TrackerVolumeColumnEffectKind kind,
		byte parameter)
	{
		if (!Enum.IsDefined(kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (parameter > 9)
			throw new ArgumentOutOfRangeException(nameof(parameter));

		Kind = kind;
		Parameter = parameter;
	}

	public TrackerVolumeColumnEffectKind Kind { get; }
	public byte Parameter { get; }
}

/// <summary>Tracker Exx pitch slide down, including shared E/F effect memory.</summary>
public sealed record TrackerPitchSlideDownPatternEffect(byte Parameter) : PatternEffect;

/// <summary>Tracker Fxx pitch slide up, including shared E/F effect memory.</summary>
public sealed record TrackerPitchSlideUpPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Gxx tone portamento. A note in the same cell becomes the target
/// pitch rather than retriggering an already-active voice.
/// </summary>
public sealed record TonePortamentoPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Lxx tone portamento plus note-volume slide. A same-cell note
/// becomes the portamento target. Tone speed is recalled from Gxx memory;
/// the visible parameter belongs to shared Dxx/Kxx/Lxx volume-slide memory.
/// </summary>
public sealed record TonePortamentoVolumeSlidePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>Tracker Jxy arpeggio, including J00 whole-byte memory.</summary>
public sealed record ArpeggioPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Rxy tremolo. High nibble is speed, low nibble depth; zero nibbles
/// independently recall the previous component.
/// </summary>
public sealed record TremoloPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Ixy tremor. The high nibble is the audible phase length and the
/// low nibble is the muted phase length; zero nibbles still last one tick
/// in modern Impulse Tracker semantics. I00 recalls whole-byte memory.
/// </summary>
public sealed record TremorPatternEffect(byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Yxy panbrello. Speed and depth use independent nibble memory;
/// the modulation is applied around the channel's persistent base panning.
/// </summary>
public sealed record PanbrelloPatternEffect(byte Parameter) : PatternEffect;

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
/// Tracker S5x panbrello waveform selection. Values 0..3 select sine,
/// ramp-down, square, and random and reset panbrello phase to zero.
/// Values 4..15 are accepted but ignored like IT.
/// </summary>
public sealed record TrackerPanbrelloWaveformPatternEffect : PatternEffect
{
	public TrackerPanbrelloWaveformPatternEffect(byte value)
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

/// <summary>
/// Tracker S73/S74/S75/S76 new-note-action override. The action applies to the
/// currently playing voice and is reset when a later note starts with its own
/// snapshotted source configuration.
/// </summary>
public sealed record TrackerNewNoteActionPatternEffect : PatternEffect
{
	public TrackerNewNoteActionPatternEffect(
		NoteDisplacementAction action)
	{
		if (!Enum.IsDefined(action))
			throw new ArgumentOutOfRangeException(nameof(action));

		Action = action;
	}

	public NoteDisplacementAction Action { get; }
}

/// <summary>
/// Tracker S77-S7C envelope enable/disable. IT has three envelope enable bits:
/// volume, panning, and a shared pitch/filter slot. Heresy keeps pitch and
/// filter as separate native slots, so PitchOrFilter resolves to both.
/// </summary>
public sealed record TrackerEnvelopeControlPatternEffect : PatternEffect
{
	public TrackerEnvelopeControlPatternEffect(
		TrackerEnvelopeControlTarget target,
		bool enabled)
	{
		if (!Enum.IsDefined(target))
			throw new ArgumentOutOfRangeException(nameof(target));

		Target = target;
		Enabled = enabled;
	}

	public TrackerEnvelopeControlTarget Target { get; }
	public bool Enabled { get; }
}

/// <summary>
/// Tracker S8x 4-bit panning. The low nibble maps through IT's 0..256
/// panning scale before being projected onto Heresy's X spatial axis.
/// </summary>
public sealed record TrackerPanningPatternEffect : PatternEffect
{
	public TrackerPanningPatternEffect(byte value)
	{
		if (value > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Tracker S91 surround. Impulse Tracker represents surround as a special
/// persistent panning state rather than an ordinary numeric pan position.
/// </summary>
public sealed record TrackerSurroundPatternEffect : PatternEffect;

/// <summary>
/// IT volume-column absolute panning. The decimal value spans the player's
/// native 0..64 pan coordinate: 0 is full left, 32 center, 64 full right.
/// </summary>
public sealed record TrackerVolumeColumnPanningPatternEffect : PatternEffect
{
	public TrackerVolumeColumnPanningPatternEffect(byte value)
	{
		if (value > 64)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Tracker Xxx 8-bit panning. IT quantizes the byte onto its internal 0..64
/// pan coordinate using (xx + 2) >> 2 before applying it.
/// </summary>
public sealed record TrackerPanning8BitPatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Pxx panning slide with whole-byte effect memory.
/// </summary>
public sealed record TrackerPanningSlidePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Mxx channel volume. Values 0..64 are valid; larger byte values are
/// preserved as tracker input and ignored during common pattern processing.
/// </summary>
public sealed record TrackerChannelVolumePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Nxx channel-volume slide with whole-byte effect memory.
/// </summary>
public sealed record TrackerChannelVolumeSlidePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Vxx global volume. Values 0..128 are valid; larger byte values are
/// preserved as tracker input and ignored during common pattern processing.
/// </summary>
public sealed record TrackerGlobalVolumePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Wxx global-volume slide. Effect memory belongs to the originating
/// physical channel even though the affected value is session-global.
/// </summary>
public sealed record TrackerGlobalVolumeSlidePatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Fine tracker-style vibrato (IT Uxy semantics). Speed/depth memory is shared
/// with normal Hxy vibrato; the pitch excursion is one quarter as deep.
/// </summary>
public sealed record FineVibratoPatternEffect(
	byte Parameter) : PatternEffect;

/// <summary>
/// Tracker Kxx vibrato plus note-volume slide. The parameter belongs to the
/// shared Dxx/Kxx/Lxx volume-slide memory; vibrato resumes the channel's
/// current Hxx/Uxx speed, effective depth, and waveform.
/// </summary>
public sealed record VibratoVolumeSlidePatternEffect(
	byte Parameter) : PatternEffect;
