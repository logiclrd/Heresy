using System;
using System.Collections.Generic;

using Heresy.Core.Envelopes;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.UserInterface.PatternEditing;

public sealed record TrackerEffectCommand(
	char Letter, string Name, Func<byte, PatternEffect> Create)
{
	public string DisplayName => $"{Letter} — {Name}";
}

/// <summary>One ordered source of truth for IT commands supported by both
/// keyboard entry and the effect-picker menus. Sxx always has a valid fallback.</summary>
public static class PatternEffectCodec
{
	private static readonly IReadOnlyList<TrackerEffectCommand> Commands =
		Array.AsReadOnly(new TrackerEffectCommand[]
		{
			new('A', "Set Speed", p => new TrackerSpeedPatternEffect(p)),
			new('B', "Position Jump", p => new TrackerOrderJumpPatternEffect(p)),
			new('C', "Pattern Break", p => new TrackerPatternBreakPatternEffect(p)),
			new('D', "Volume Slide", p => new TrackerVolumeSlidePatternEffect(p)),
			new('E', "Portamento Down", p => new TrackerPitchSlideDownPatternEffect(p)),
			new('F', "Portamento Up", p => new TrackerPitchSlideUpPatternEffect(p)),
			new('G', "Tone Portamento", p => new TonePortamentoPatternEffect(p)),
			new('H', "Vibrato", p => new VibratoPatternEffect(p)),
			new('I', "Tremor", p => new TremorPatternEffect(p)),
			new('J', "Arpeggio", p => new ArpeggioPatternEffect(p)),
			new('K', "Vibrato + Volume Slide", p => new VibratoVolumeSlidePatternEffect(p)),
			new('L', "Tone Portamento + Volume Slide", p => new TonePortamentoVolumeSlidePatternEffect(p)),
			new('M', "Set Channel Volume", p => new TrackerChannelVolumePatternEffect(p)),
			new('N', "Channel Volume Slide", p => new TrackerChannelVolumeSlidePatternEffect(p)),
			new('O', "Sample Offset", p => new SampleOffsetPatternEffect(p)),
			new('P', "Panning Slide", p => new TrackerPanningSlidePatternEffect(p)),
			new('Q', "Retrigger Note", p => new RetriggerPatternEffect(p)),
			new('R', "Tremolo", p => new TremoloPatternEffect(p)),
			new('S', "Special", CreateSpecialOrUndefined),
			new('T', "Tempo", p => new TrackerTempoPatternEffect(p)),
			new('U', "Fine Vibrato", p => new FineVibratoPatternEffect(p)),
			new('V', "Set Global Volume", p => new TrackerGlobalVolumePatternEffect(p)),
			new('W', "Global Volume Slide", p => new TrackerGlobalVolumeSlidePatternEffect(p)),
			new('X', "Set Panning", p => new TrackerPanning8BitPatternEffect(p)),
			new('Y', "Panbrello", p => new PanbrelloPatternEffect(p)),
			new('Z', "MIDI Macro", p => new TrackerMidiMacroPatternEffect(p)),
		});

	public static IReadOnlyList<TrackerEffectCommand> TrackerCommands => Commands;

	public static int TrackerColorKey(char command)
	{
		char letter = char.ToUpperInvariant(command);
		if (letter < 'A' || letter > 'Z')
			throw new ArgumentOutOfRangeException(nameof(command));
		return letter - 'A';
	}
	public static bool IsTrackerStyle(PatternEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);
		return effect is EmptyTrackerPatternEffect
            or UndefinedTrackerSpecialPatternEffect
			|| TryDecodeTracker(effect, out _, out _);
	}

	public static bool TryDecodeTrackerSlot(
		PatternEffect effect,
		out char? command,
		out byte parameter)
	{
		ArgumentNullException.ThrowIfNull(effect);

		if (effect is EmptyTrackerPatternEffect empty)
		{
			command = null;
			parameter = empty.Parameter;
			return true;
		}

		if (TryDecodeTracker(
			effect,
			out char concreteCommand,
			out parameter))
		{
			command = concreteCommand;
			return true;
		}

		command = null;
		parameter = default;
		return false;
	}

	public static bool TryDecodeTracker(
		PatternEffect effect,
		out char command,
		out byte parameter)
	{
		ArgumentNullException.ThrowIfNull(effect);

		switch (effect)
		{
            case UndefinedTrackerSpecialPatternEffect undefined:
                command = 'S'; parameter = undefined.Parameter; return true;
            case TrackerSpeedPatternEffect speed:
                command = 'A'; parameter = speed.Parameter; return true;
			case TrackerOrderJumpPatternEffect value:
				command = 'B'; parameter = value.Order; return true;
			case TrackerPatternBreakPatternEffect value:
				command = 'C'; parameter = value.Row; return true;
			case TrackerVolumeSlidePatternEffect value:
				command = 'D'; parameter = value.Parameter; return true;
			case TrackerPitchSlideDownPatternEffect value:
				command = 'E'; parameter = value.Parameter; return true;
			case TrackerPitchSlideUpPatternEffect value:
				command = 'F'; parameter = value.Parameter; return true;
			case TonePortamentoPatternEffect value:
				command = 'G'; parameter = value.Parameter; return true;
			case VibratoPatternEffect value:
				command = 'H'; parameter = value.Parameter; return true;
			case TremorPatternEffect value:
				command = 'I'; parameter = value.Parameter; return true;
			case ArpeggioPatternEffect value:
				command = 'J'; parameter = value.Parameter; return true;
			case VibratoVolumeSlidePatternEffect value:
				command = 'K'; parameter = value.Parameter; return true;
			case TonePortamentoVolumeSlidePatternEffect value:
				command = 'L'; parameter = value.Parameter; return true;
			case TrackerChannelVolumePatternEffect value:
				command = 'M'; parameter = value.Parameter; return true;
			case TrackerChannelVolumeSlidePatternEffect value:
				command = 'N'; parameter = value.Parameter; return true;
			case SampleOffsetPatternEffect value:
				command = 'O'; parameter = value.Parameter; return true;
			case TrackerPanningSlidePatternEffect value:
				command = 'P'; parameter = value.Parameter; return true;
			case RetriggerPatternEffect value:
				command = 'Q'; parameter = value.Parameter; return true;
			case TremoloPatternEffect value:
				command = 'R'; parameter = value.Parameter; return true;
			case TrackerTempoPatternEffect value:
				command = 'T'; parameter = value.Parameter; return true;
			case FineVibratoPatternEffect value:
				command = 'U'; parameter = value.Parameter; return true;
			case TrackerGlobalVolumePatternEffect value:
				command = 'V'; parameter = value.Parameter; return true;
			case TrackerGlobalVolumeSlidePatternEffect value:
				command = 'W'; parameter = value.Parameter; return true;
			case TrackerPanning8BitPatternEffect value:
				command = 'X'; parameter = value.Parameter; return true;
			case PanbrelloPatternEffect value:
				command = 'Y'; parameter = value.Parameter; return true;
			case TrackerMidiMacroPatternEffect value:
				command = 'Z'; parameter = value.Parameter; return true;

			case TrackerGlissandoControlPatternEffect value:
				command = 'S'; parameter = (byte)(0x10 | value.Value); return true;
			case TrackerVibratoWaveformPatternEffect value:
				command = 'S'; parameter = (byte)(0x30 | value.Value); return true;
			case TrackerTremoloWaveformPatternEffect value:
				command = 'S'; parameter = (byte)(0x40 | value.Value); return true;
			case TrackerPanbrelloWaveformPatternEffect value:
				command = 'S'; parameter = (byte)(0x50 | value.Value); return true;
			case TrackerFinePatternDelayPatternEffect value:
				command = 'S'; parameter = (byte)(0x60 | value.ExtraTicks); return true;
			case TrackerPastNoteActionPatternEffect value:
				command = 'S'; parameter = (byte)(0x70 | (byte)value.Action); return true;
			case TrackerNewNoteActionPatternEffect value:
				command = 'S'; parameter = (byte)(0x73 + (byte)value.Action); return true;
			case TrackerEnvelopeControlPatternEffect value:
				command = 'S'; parameter = EncodeEnvelope(value); return true;
			case TrackerPanningPatternEffect value:
				command = 'S'; parameter = (byte)(0x80 | value.Value); return true;
			case TrackerSurroundPatternEffect:
				command = 'S'; parameter = 0x91; return true;
			case SampleOffsetHighPatternEffect value:
				command = 'S'; parameter = (byte)(0xA0 | value.HighOffset); return true;
			case TrackerPatternLoopPatternEffect value:
				command = 'S'; parameter = (byte)(0xB0 | value.RepeatCount); return true;
			case TrackerNoteCutPatternEffect value:
				command = 'S'; parameter = (byte)(0xC0 | value.Tick); return true;
			case TrackerNoteDelayPatternEffect value:
				command = 'S'; parameter = (byte)(0xD0 | value.Tick); return true;
			case TrackerPatternDelayPatternEffect value:
				command = 'S'; parameter = (byte)(0xE0 | value.ExtraRows); return true;
			case TrackerMidiMacroSelectPatternEffect value:
				command = 'S'; parameter = (byte)(0xF0 | value.Macro); return true;
		}

		command = default;
		parameter = default;
		return false;
	}

	public static bool TryCreateTracker(
		char command,
		byte parameter,
		out PatternEffect? effect)
	{
		char letter = char.ToUpperInvariant(command);
		if (letter < 'A' || letter > 'Z')
		{
			effect = null;
			return false;
		}

		// Catalogue index, menu label, keyboard semantics and sticker
		// colour are all derived from the same canonical IT letter.
		TrackerEffectCommand description = Commands[letter - 'A'];
		effect = description.Create(parameter);
		return true;
	}

    /// <summary>Editing Sxx always succeeds for any hex byte. Each
    /// nibble change selects the exact semantic effect or its undefined
    /// fallback, without inventing a different subcommand.</summary>
    private static PatternEffect CreateSpecialOrUndefined(byte parameter)
    {
        try
        {
            return CreateSpecial(parameter)
                ?? new UndefinedTrackerSpecialPatternEffect(parameter);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new UndefinedTrackerSpecialPatternEffect(parameter);
        }
    }

	private static PatternEffect? CreateSpecial(byte parameter)
	{
		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		return high switch
		{
			0x1 => new TrackerGlissandoControlPatternEffect(low),
			0x3 => new TrackerVibratoWaveformPatternEffect(low),
			0x4 => new TrackerTremoloWaveformPatternEffect(low),
			0x5 => new TrackerPanbrelloWaveformPatternEffect(low),
			0x6 => new TrackerFinePatternDelayPatternEffect(low),
			0x7 => CreateS7(low),
			0x8 => new TrackerPanningPatternEffect(low),
			0x9 when low == 1 => new TrackerSurroundPatternEffect(),
			0xA => new SampleOffsetHighPatternEffect(low),
			0xB => new TrackerPatternLoopPatternEffect(low),
			0xC => new TrackerNoteCutPatternEffect(low),
			0xD => new TrackerNoteDelayPatternEffect(low),
			0xE => new TrackerPatternDelayPatternEffect(low),
			0xF => new TrackerMidiMacroSelectPatternEffect(low),
			_ => null,
		};
	}

	private static PatternEffect? CreateS7(byte low)
		=> low switch
		{
			0x0 => new TrackerPastNoteActionPatternEffect(TrackerPastNoteAction.Cut),
			0x1 => new TrackerPastNoteActionPatternEffect(TrackerPastNoteAction.Off),
			0x2 => new TrackerPastNoteActionPatternEffect(TrackerPastNoteAction.Fade),
			0x3 => new TrackerNewNoteActionPatternEffect(NoteDisplacementAction.Cut),
			0x4 => new TrackerNewNoteActionPatternEffect(NoteDisplacementAction.Continue),
			0x5 => new TrackerNewNoteActionPatternEffect(NoteDisplacementAction.Off),
			0x6 => new TrackerNewNoteActionPatternEffect(NoteDisplacementAction.Fade),
			0x7 => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Volume, false),
			0x8 => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Volume, true),
			0x9 => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Panning, false),
			0xA => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Panning, true),
			0xB => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.PitchOrFilter, false),
			0xC => new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.PitchOrFilter, true),
			_ => null,
		};

	private static byte EncodeEnvelope(TrackerEnvelopeControlPatternEffect effect)
		=> (effect.Target, effect.Enabled) switch
		{
			(TrackerEnvelopeControlTarget.Volume, false) => 0x77,
			(TrackerEnvelopeControlTarget.Volume, true) => 0x78,
			(TrackerEnvelopeControlTarget.Panning, false) => 0x79,
			(TrackerEnvelopeControlTarget.Panning, true) => 0x7A,
			(TrackerEnvelopeControlTarget.PitchOrFilter, false) => 0x7B,
			(TrackerEnvelopeControlTarget.PitchOrFilter, true) => 0x7C,
			_ => throw new ArgumentOutOfRangeException(nameof(effect)),
		};
}
