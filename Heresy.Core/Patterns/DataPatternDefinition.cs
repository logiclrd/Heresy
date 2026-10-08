using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Patterns;

/// <summary>
/// Data-driven tracker pattern. The editable grid stores semantic note/effect
/// values and translates them into the same raw NoteEvent representation used
/// by scripted patterns before the common PatternNoteProcessor runs.
/// </summary>
public sealed class DataPatternDefinition : PatternDefinition, IDeferredSourcePatternGenerator, IReplayableRawPatternNoteGenerator
{
	public DataPatternDefinition(ObjectId id, string name) : base(id, name)
	{
		Grid = new PatternGrid(RowCount, ChannelCount);
	}

	public PatternGrid Grid { get; }

	// Compatibility bridge: the established processor continues to receive
	// all raw notes at once. New sequencing cursors may instead suspend the
	// underlying generator at any Emit or silent Advance step.
	public void GenerateRawNotes(
		SequencingContext context,
		INoteReceiver output,
		out double rowCount)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);
		foreach (RawPatternStep step in EnumerateRawSteps(context))
		{
			if (step is RawPatternStep.Emit emission)
				output.Append(emission.Note);
		}
		rowCount = RowCount;
	}

	public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		long nextProgressRow = 100;
		long lastProgressRow = 0;
		for (int row = 0; row < RowCount; row++)
		{
			bool emitted = false;
			for (int channel = 0; channel < ChannelCount; channel++)
			{
				PatternCell? cell = Grid[row, channel];
				if (cell is null || cell.IsEmpty)
					continue;
				List<NoteCommand> channelCommands = [];
			List<NoteCommand> globalCommands = [];

			// Raw-note callers retain the historical immediate resolution
			// contract. Song compilation instead records Source-column changes
			// and defers omitted-source lookup until this row executes, after
			// any flattened child has changed the mapped channel state.
			bool deferSource = context.ResolvePatternSourcesAtRowTime;
			ObjectId explicitSourceId = !cell.SourceId.IsNone
				? cell.SourceId
				: cell.Note is StartPatternNote inline
					? inline.SourceId
					: ObjectId.None;

			SequencingChannelState channelState =
				context.GetPhysicalChannelState(channel);
			if (deferSource)
			{
				if (!explicitSourceId.IsNone)
					channelCommands.Add(
						new SelectPatternSourceCommand(explicitSourceId));
			}
			else if (!explicitSourceId.IsNone)
			{
				channelState.CurrentSourceId = explicitSourceId;
			}
			ObjectId resolvedSourceId = deferSource
				? explicitSourceId
				: channelState.CurrentSourceId;

			StartNoteCommand? tonePortamentoTarget = null;
			bool hasTonePortamento = false;
			foreach (PatternEffect effect in cell.Effects)
			{
				if (effect is TonePortamentoPatternEffect
					or TonePortamentoVolumeSlidePatternEffect
					or TrackerVolumeColumnPatternEffect
						{ Kind: TrackerVolumeColumnEffectKind.TonePortamento })
				{
					hasTonePortamento = true;
					break;
				}
			}

			bool startsNewNote =
				cell.Note is StartPatternNote
					&& !hasTonePortamento
					&& (deferSource || !resolvedSourceId.IsNone);

			if (cell.Note is StartPatternNote start && hasTonePortamento)
			{
				// Tone portamento uses the note as a pitch target rather than
				// starting it immediately. Volume therefore follows the
				// current-note command path below.
				tonePortamentoTarget =
					deferSource || !resolvedSourceId.IsNone
						? TranslateStartNote(start, resolvedSourceId)
						: null;
			}
			else if (cell.Note is StartPatternNote startNote)
			{
				if (startsNewNote)
				{
					channelCommands.Add(
						TranslateStartNote(
							startNote,
							resolvedSourceId,
							cell.Volume));
				}
			}
			else if (cell.Note is not null)
			{
				channelCommands.Add(TranslateNote(cell.Note));
			}

			if (cell.Volume.HasValue
				&& !startsNewNote
				&& cell.Note is not PatternNoteCut)
			{
				// A cut ends the current note immediately, so a same-row
				// pattern volume has no current voice to affect and is ignored.
				// Note-off is deliberately different: its releasing voice stays
				// attached to the channel and remains volume-controllable.
				channelCommands.Add(
					new SetNoteVolumeCommand(cell.Volume.Value));
			}

			foreach (PatternEffect effect in cell.Effects)
			{
				if (effect is EmptyTrackerPatternEffect)
					continue;

				NoteCommand command;
				bool isGlobal;

				if (effect is TonePortamentoPatternEffect tonePortamento)
				{
					command = new ApplyTonePortamentoCommand(
						tonePortamento.Parameter,
						tonePortamentoTarget);
					tonePortamentoTarget = null;
					isGlobal = false;
				}
				else if (effect is TonePortamentoVolumeSlidePatternEffect combined)
				{
					command = new ApplyTonePortamentoVolumeSlideCommand(
						combined.Parameter,
						tonePortamentoTarget);
					tonePortamentoTarget = null;
					isGlobal = false;
				}
				else if (effect is TrackerVolumeColumnPatternEffect volumeColumn)
				{
					command = new ApplyTrackerVolumeColumnCommand(
						volumeColumn.Kind,
						volumeColumn.Parameter,
						volumeColumn.Kind == TrackerVolumeColumnEffectKind.TonePortamento
							? tonePortamentoTarget
							: null);
					if (volumeColumn.Kind == TrackerVolumeColumnEffectKind.TonePortamento)
						tonePortamentoTarget = null;
					isGlobal = false;
				}
				else
				{
					command = TranslateEffect(effect, out isGlobal);
				}

				if (isGlobal)
					globalCommands.Add(command);
				else
					channelCommands.Add(command);
			}

			MusicalTime offset = new(TimeSpan.Zero, row);

			if (globalCommands.Count != 0)
			{
				emitted = true;
				yield return new RawPatternStep.Emit(new NoteEvent(
					offset,
					ChannelTarget.Global,
					globalCommands));
			}

			if (channelCommands.Count != 0)
			{
				emitted = true;
				yield return new RawPatternStep.Emit(new NoteEvent(
					offset,
					ChannelTarget.Physical(channel),
					channelCommands));
			}
			}

			// A long silent span must still cooperate. This is musical
			// progress, not an audible or state-mutating note.
			if (emitted)
				nextProgressRow = (long)row + 100;
			else if ((long)row + 1 >= nextProgressRow)
			{
				lastProgressRow = nextProgressRow;
				yield return new RawPatternStep.Advance(nextProgressRow);
				nextProgressRow += 100;
			}
		}

		if (lastProgressRow != RowCount)
			yield return new RawPatternStep.Advance(RowCount);
	}

	protected override void OnDimensionsChanged(int previousRowCount, int previousChannelCount)
	{
		Grid.Resize(RowCount, ChannelCount);
	}

	private static StartNoteCommand TranslateStartNote(
		StartPatternNote start,
		ObjectId sourceId,
		double? volume = null)
		=> new(
			sourceId,
			start.PitchMultiplier,
			start.PlaybackSpeedMultiplier,
			start.Mixdown,
			volume);

	private static NoteCommand TranslateNote(PatternNoteEntry note)
		=> note switch
		{
			PatternNoteOff => new NoteOffCommand(),
			PatternNoteCut => new NoteCutCommand(),
			_ => throw new NotSupportedException($"Unsupported pattern note type: {note.GetType().FullName}"),
		};

	private static NoteCommand TranslateEffect(PatternEffect effect, out bool isGlobal)
	{
		ArgumentNullException.ThrowIfNull(effect);

		isGlobal = false;

		switch (effect)
		{
			case TrackerOrderJumpPatternEffect jump:
				return new ApplyTrackerOrderJumpCommand(jump.Order);

			case TrackerPatternBreakPatternEffect patternBreak:
				return new ApplyTrackerPatternBreakCommand(patternBreak.Row);

			case SetTempoPatternEffect tempo:
				isGlobal = true;
				return new SetTempoCommand(tempo.TicksPerDiachron);

			case SetSpeedPatternEffect speed:
				isGlobal = true;
				return new SetSpeedCommand(speed.TicksPerRow);

			case TrackerTempoPatternEffect tempo:
				return new ApplyTrackerTempoCommand(tempo.Parameter);

			case SetNoteVolumePatternEffect volume:
				return new SetNoteVolumeCommand(volume.Volume);

			case SetOverallChannelVolumePatternEffect volume:
				return new SetOverallChannelVolumeCommand(volume.Volume);

			case SetPlaybackFrequencyPatternEffect frequency:
				return new SetPlaybackFrequencyCommand(frequency.Frequency);

			case SetPlaybackOffsetPatternEffect offset:
				return new SetPlaybackOffsetCommand(offset.Offset);

			case VibratoPatternEffect vibrato:
				return new ApplyVibratoCommand(vibrato.Parameter);

			case FineVibratoPatternEffect vibrato:
				return new ApplyFineVibratoCommand(vibrato.Parameter);

			case VibratoVolumeSlidePatternEffect combined:
				return new ApplyVibratoVolumeSlideCommand(
					combined.Parameter);

			case TrackerVibratoWaveformPatternEffect waveform:
				return new ApplyTrackerVibratoWaveformCommand(waveform.Value);

			case TrackerMidiMacroSelectPatternEffect macro:
				return new ApplyTrackerMidiMacroSelectCommand(macro.Macro);

			case TrackerMidiMacroPatternEffect macro:
				return new ApplyTrackerMidiMacroCommand(macro.Parameter);

			case SetResonantFilterPatternEffect filter:
				return new SetResonantFilterCommand(
					filter.Cutoff,
					filter.Resonance);

			case PitchSlidePatternEffect slide:
				return new SetPitchSlideCommand(
					slide.LinearUnitsPerTick);

			case NoteVolumeSlidePatternEffect slide:
				return new SetNoteVolumeSlideCommand(
					slide.TrackerUnitsPerTick);

			case TrackerVolumeSlidePatternEffect slide:
				return new ApplyVolumeSlideCommand(slide.Parameter);

			case TrackerChannelVolumePatternEffect volume:
				return new ApplyTrackerChannelVolumeCommand(
					volume.Parameter);

			case TrackerChannelVolumeSlidePatternEffect slide:
				return new ApplyChannelVolumeSlideCommand(
					slide.Parameter);

			case TrackerGlobalVolumePatternEffect volume:
				return new ApplyTrackerGlobalVolumeCommand(
					volume.Parameter);

			case TrackerGlobalVolumeSlidePatternEffect slide:
				return new ApplyGlobalVolumeSlideCommand(
					slide.Parameter);

			case TrackerPitchSlideDownPatternEffect slide:
				return new ApplyPitchSlideDownCommand(slide.Parameter);

			case TrackerPitchSlideUpPatternEffect slide:
				return new ApplyPitchSlideUpCommand(slide.Parameter);

			case ArpeggioPatternEffect arpeggio:
				return new ApplyArpeggioCommand(arpeggio.Parameter);

			case TremoloPatternEffect tremolo:
				return new ApplyTremoloCommand(tremolo.Parameter);

			case TremorPatternEffect tremor:
				return new ApplyTremorCommand(tremor.Parameter);

			case PanbrelloPatternEffect panbrello:
				return new ApplyPanbrelloCommand(
					panbrello.Parameter);

			case TrackerTremoloWaveformPatternEffect waveform:
				return new ApplyTrackerTremoloWaveformCommand(waveform.Value);

			case TrackerPanbrelloWaveformPatternEffect waveform:
				return new ApplyTrackerPanbrelloWaveformCommand(
					waveform.Value);

			case TrackerGlissandoControlPatternEffect glissando:
				return new ApplyTrackerGlissandoControlCommand(glissando.Value);

			case RetriggerPatternEffect retrigger:
				return new ApplyRetriggerCommand(retrigger.Parameter);

			case SampleOffsetPatternEffect offset:
				return new ApplySampleOffsetCommand(offset.Parameter);

			case SampleOffsetHighPatternEffect offset:
				return new ApplySampleOffsetHighCommand(offset.HighOffset);

			case TrackerNoteCutPatternEffect cut:
				return new ApplyTrackerNoteCutCommand(cut.Tick);

			case TrackerNoteDelayPatternEffect delay:
				return new ApplyTrackerNoteDelayCommand(delay.Tick);

			case TrackerPatternDelayPatternEffect delay:
				return new ApplyTrackerPatternDelayCommand(delay.ExtraRows);

			case TrackerFinePatternDelayPatternEffect delay:
				return new ApplyTrackerFinePatternDelayCommand(delay.ExtraTicks);

			case TrackerPatternLoopPatternEffect loop:
				return new ApplyTrackerPatternLoopCommand(loop.RepeatCount);

			case TrackerPastNoteActionPatternEffect pastNote:
				return new ApplyTrackerPastNoteActionCommand(pastNote.Action);

			case TrackerNewNoteActionPatternEffect newNoteAction:
				return new ApplyTrackerNewNoteActionCommand(
					newNoteAction.Action);

			case TrackerEnvelopeControlPatternEffect envelope:
				return new ApplyTrackerEnvelopeControlCommand(
					envelope.Target,
					envelope.Enabled);

			case TrackerSurroundPatternEffect:
				return new SetSurroundCommand(true);

			case TrackerPanningPatternEffect panning:
				return new ApplyTrackerPanningCommand(panning.Value);

			case TrackerVolumeColumnPanningPatternEffect panning:
				return new ApplyTrackerVolumeColumnPanningCommand(
					panning.Value);

			case TrackerPanning8BitPatternEffect panning:
				return new ApplyTrackerPanning8BitCommand(
					panning.Parameter);

			case TrackerPanningSlidePatternEffect slide:
				return new ApplyPanningSlideCommand(slide.Parameter);

			default:
				throw new NotSupportedException($"Unsupported pattern effect type: {effect.GetType().FullName}");
		}
	}
}
