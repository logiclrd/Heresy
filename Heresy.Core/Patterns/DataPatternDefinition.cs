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

			if (cell.Note is not null)
				channelCommands.Add(TranslateNote(cell.Note));

			foreach (PatternEffect effect in cell.Effects)
			{
				NoteCommand command = TranslateEffect(effect, out bool isGlobal);
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

			default:
				throw new NotSupportedException($"Unsupported pattern effect type: {effect.GetType().FullName}");
		}
	}
}
