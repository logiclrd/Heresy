using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.UserInterface.PatternEditing;

public static class PatternEffectCodec
{
	public static bool IsTrackerStyle(PatternEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);
		return effect is EmptyTrackerPatternEffect
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
		command = char.ToUpperInvariant(command);
		effect = command switch
		{
			'B' => new TrackerOrderJumpPatternEffect(parameter),
			'C' => new TrackerPatternBreakPatternEffect(parameter),
			'D' => new TrackerVolumeSlidePatternEffect(parameter),
			'E' => new TrackerPitchSlideDownPatternEffect(parameter),
			'F' => new TrackerPitchSlideUpPatternEffect(parameter),
			'G' => new TonePortamentoPatternEffect(parameter),
			'H' => new VibratoPatternEffect(parameter),
			'I' => new TremorPatternEffect(parameter),
			'J' => new ArpeggioPatternEffect(parameter),
			'K' => new VibratoVolumeSlidePatternEffect(parameter),
			'L' => new TonePortamentoVolumeSlidePatternEffect(parameter),
			'M' => new TrackerChannelVolumePatternEffect(parameter),
			'N' => new TrackerChannelVolumeSlidePatternEffect(parameter),
			'O' => new SampleOffsetPatternEffect(parameter),
			'P' => new TrackerPanningSlidePatternEffect(parameter),
			'Q' => new RetriggerPatternEffect(parameter),
			'R' => new TremoloPatternEffect(parameter),
			'S' => CreateSpecial(parameter),
			'T' => new TrackerTempoPatternEffect(parameter),
			'U' => new FineVibratoPatternEffect(parameter),
			'V' => new TrackerGlobalVolumePatternEffect(parameter),
			'W' => new TrackerGlobalVolumeSlidePatternEffect(parameter),
			'X' => new TrackerPanning8BitPatternEffect(parameter),
			'Y' => new PanbrelloPatternEffect(parameter),
			'Z' => new TrackerMidiMacroPatternEffect(parameter),
			_ => null,
		};
		return effect is not null;
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
