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
public sealed class DataPatternDefinition : PatternDefinition, IRawPatternNoteGenerator
{
	public DataPatternDefinition(ObjectId id, string name) : base(id, name)
	{
		Grid = new PatternGrid(RowCount, ChannelCount);
	}

	public PatternGrid Grid { get; }

	public void GenerateRawNotes(
		SequencingContext context,
		INoteReceiver output,
		out double rowCount)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);

		foreach ((int row, int channel, PatternCell cell) in Grid.EnumerateNonEmptyCells())
		{
			List<NoteCommand> channelCommands = [];
			List<NoteCommand> globalCommands = [];

			StartNoteCommand? tonePortamentoTarget = null;
			bool hasTonePortamento = false;
			foreach (PatternEffect effect in cell.Effects)
			{
				if (effect is TonePortamentoPatternEffect)
				{
					hasTonePortamento = true;
					break;
				}
			}

			if (cell.Note is StartPatternNote start && hasTonePortamento)
			{
				tonePortamentoTarget = (StartNoteCommand)TranslateNote(start);
			}
			else if (cell.Note is not null)
			{
				channelCommands.Add(TranslateNote(cell.Note));
			}

			foreach (PatternEffect effect in cell.Effects)
			{
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
				output.Append(new NoteEvent(
					offset,
					ChannelTarget.Global,
					globalCommands));
			}

			if (channelCommands.Count != 0)
			{
				output.Append(new NoteEvent(
					offset,
					ChannelTarget.Physical(channel),
					channelCommands));
			}
		}

		rowCount = RowCount;
	}

	protected override void OnDimensionsChanged(int previousRowCount, int previousChannelCount)
	{
		Grid.Resize(RowCount, ChannelCount);
	}

	private static NoteCommand TranslateNote(PatternNoteEntry note)
		=> note switch
		{
			StartPatternNote start => new StartNoteCommand(
				start.SourceId,
				start.PitchMultiplier,
				start.PlaybackSpeedMultiplier,
				start.Mixdown),
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
			case SetTempoPatternEffect tempo:
				isGlobal = true;
				return new SetTempoCommand(tempo.TicksPerDiachron);

			case SetSpeedPatternEffect speed:
				isGlobal = true;
				return new SetSpeedCommand(speed.TicksPerRow);

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

			case TrackerVibratoWaveformPatternEffect waveform:
				return new ApplyTrackerVibratoWaveformCommand(waveform.Value);

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

			case TrackerTremoloWaveformPatternEffect waveform:
				return new ApplyTrackerTremoloWaveformCommand(waveform.Value);

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

			case TrackerPanningPatternEffect panning:
				return new ApplyTrackerPanningCommand(panning.Value);

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
