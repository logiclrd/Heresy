using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class DataPatternDefinitionTests
{
	[Test]
	public void EmptyPatternGeneratesNoEventsAndReportsConfiguredRowCount()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Empty")
		{
			RowCount = 32,
			ChannelCount = 4,
		};
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out double rowCount);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(0));
		Assert.That(rowCount, Is.EqualTo(32.0));
	}

	[Test]
	public void StartNoteTranslatesToStartNoteCommandOnPhysicalChannel()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		pattern.Grid.GetOrCreateCell(3, 2).Note = new StartPatternNote(
			(ObjectId)17U,
			pitchMultiplier: 1.5,
			playbackSpeedMultiplier: 0.5,
			mixdown: true);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.RowOffset, Is.EqualTo(3.0));
		Assert.That(schedule[0].Target, Is.EqualTo(ChannelTarget.Physical(2)));
		Assert.That(schedule[0].Commands.Count, Is.EqualTo(1));

		StartNoteCommand command = (StartNoteCommand)schedule[0].Commands[0];
		Assert.That(command.SourceId, Is.EqualTo((ObjectId)17U));
		Assert.That(command.PitchMultiplier, Is.EqualTo(1.5));
		Assert.That(command.PlaybackSpeedMultiplier, Is.EqualTo(0.5));
		Assert.That(command.Mixdown, Is.True);
	}

	[Test]
	public void NoteOffAndCutTranslateFromNoteColumn()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		pattern.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<NoteOffCommand>());
		Assert.That(schedule[1].Commands[0], Is.TypeOf<NoteCutCommand>());
	}

	[Test]
	public void TimingEffectsBecomeSeparateGlobalEvent()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(5, 3);
		cell.Note = new PatternNoteCut();
		cell.Effects.Add(new SetTempoPatternEffect(150.0));
		cell.Effects.Add(new SetSpeedPatternEffect(4));
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.75));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(2));

		NoteEvent global = schedule[0];
		Assert.That(global.Target, Is.EqualTo(ChannelTarget.Global));
		Assert.That(global.Offset.RowOffset, Is.EqualTo(5.0));
		Assert.That(global.Commands.Count, Is.EqualTo(2));
		Assert.That(global.Commands[0], Is.EqualTo(new SetTempoCommand(150.0)));
		Assert.That(global.Commands[1], Is.EqualTo(new SetSpeedCommand(4)));

		NoteEvent channel = schedule[1];
		Assert.That(channel.Target, Is.EqualTo(ChannelTarget.Physical(3)));
		Assert.That(channel.Commands.Count, Is.EqualTo(2));
		Assert.That(channel.Commands[0], Is.TypeOf<NoteCutCommand>());
		Assert.That(channel.Commands[1], Is.EqualTo(new SetNoteVolumeCommand(0.75)));
	}

	[Test]
	public void ChannelEffectsTranslateInStoredOrder()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 1);
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		cell.Effects.Add(new SetOverallChannelVolumePatternEffect(0.25));
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440.0));
		cell.Effects.Add(new SetPlaybackOffsetPatternEffect(TimeSpan.FromMilliseconds(125)));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands[0], Is.EqualTo(new SetNoteVolumeCommand(0.5)));
		Assert.That(schedule[0].Commands[1], Is.EqualTo(new SetOverallChannelVolumeCommand(0.25)));
		Assert.That(schedule[0].Commands[2], Is.EqualTo(new SetPlaybackFrequencyCommand(440.0)));
		Assert.That(schedule[0].Commands[3], Is.EqualTo(new SetPlaybackOffsetCommand(TimeSpan.FromMilliseconds(125))));
	}

	[Test]
	public void GridEnumeratesCellsInRowMajorOrder()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 8,
			ChannelCount = 4,
		};
		pattern.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(1, 3).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(3));
		Assert.That(schedule[0].Offset.RowOffset, Is.EqualTo(1.0));
		Assert.That(schedule[0].Target, Is.EqualTo(ChannelTarget.Physical(1)));
		Assert.That(schedule[1].Offset.RowOffset, Is.EqualTo(1.0));
		Assert.That(schedule[1].Target, Is.EqualTo(ChannelTarget.Physical(3)));
		Assert.That(schedule[2].Offset.RowOffset, Is.EqualTo(3.0));
		Assert.That(schedule[2].Target, Is.EqualTo(ChannelTarget.Physical(0)));
	}

	[Test]
	public void ResizingPatternPreservesOverlapAndDropsCellsOutsideNewDimensions()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell retained = pattern.Grid.GetOrCreateCell(2, 1);
		retained.Note = new PatternNoteCut();
		PatternCell dropped = pattern.Grid.GetOrCreateCell(20, 6);
		dropped.Note = new PatternNoteOff();

		pattern.RowCount = 8;
		pattern.ChannelCount = 2;

		Assert.That(pattern.Grid.RowCount, Is.EqualTo(8));
		Assert.That(pattern.Grid.ChannelCount, Is.EqualTo(2));
		Assert.That(pattern.Grid[2, 1], Is.SameAs(retained));
		Assert.Throws<ArgumentOutOfRangeException>(() => _ = pattern.Grid[20, 1]);
		Assert.Throws<ArgumentOutOfRangeException>(() => _ = pattern.Grid[2, 6]);
	}
	[Test]
	public void EmptyTrackerEffectIsPersistableEditorSlotAndGeneratesNoCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new EmptyTrackerPatternEffect());
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands, Has.Count.EqualTo(1));
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeCommand(0.5)));
	}

	[Test]
	public void PatternVolumeMakesOtherwiseEmptyCellNonEmpty()
	{
		PatternCell cell = new()
		{
			Volume = 0.5,
		};

		Assert.That(cell.IsEmpty, Is.False);
	}

	[TestCase(-0.01)]
	[TestCase(1.01)]
	[TestCase(double.NaN)]
	[TestCase(double.PositiveInfinity)]
	public void PatternVolumeRejectsValuesOutsideNormalizedRange(double volume)
	{
		PatternCell cell = new();

		Assert.Throws<ArgumentOutOfRangeException>(
			() => cell.Volume = volume);
	}

	[Test]
	public void StartNoteFoldsPatternVolumeIntoStartCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)17U);
		cell.Volume = 0.75;
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands, Has.Count.EqualTo(1));
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(
				new StartNoteCommand(
					(ObjectId)17U,
					Volume: 0.75)));
	}

	[Test]
	public void VolumeOnlyRowBecomesSetNoteVolumeCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		pattern.Grid.GetOrCreateCell(3, 2).Volume = 0.5;
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.RowOffset, Is.EqualTo(3.0));
		Assert.That(schedule[0].Target, Is.EqualTo(ChannelTarget.Physical(2)));
		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetNoteVolumeCommand(0.5),
				}));
	}

	[Test]
	public void TonePortamentoTargetUsesVolumeAsCurrentNoteCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)17U, 2.0);
		cell.Volume = 0.25;
		cell.Effects.Add(new TonePortamentoPatternEffect(0x10));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands, Has.Count.EqualTo(2));
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeCommand(0.25)));
		ApplyTonePortamentoCommand portamento =
			(ApplyTonePortamentoCommand)schedule[0].Commands[1];
		Assert.That(portamento.TargetNote, Is.Not.Null);
		Assert.That(portamento.TargetNote!.Volume, Is.Null);
	}

}
