using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class NoteCutAndDelayPatternEffectTests
{
	[Test]
	public void DataGridTranslatesSCxAndSDxToRawCommands()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerNoteCutPatternEffect(3));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(4));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerNoteCutCommand(3)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerNoteDelayCommand(4)));
	}

	[Test]
	public void SC3CutsAtTrackerTickThree()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteCutPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			schedule[1].Commands,
			Is.EqualTo(new NoteCommand[] { new NoteCutCommand() }));
	}

	[TestCase(0)]
	[TestCase(1)]
	public void SC0AndSC1BothCutOnFirstPostStartTick(byte tick)
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteCutPatternEffect(tick));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(20)));
		Assert.That(
			schedule[1].Commands[0],
			Is.TypeOf<NoteCutCommand>());
	}

	[TestCase(6)]
	[TestCase(15)]
	public void SCAtOrBeyondSpeedDoesNotFireInRow(byte tick)
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteCutPatternEffect(tick));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
	}

	[Test]
	public void SD3DelaysEntireResolvedNoteSetupAtomically()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		cell.Effects.Add(new SampleOffsetPatternEffect(0x01));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(schedule[0].Commands.Count, Is.EqualTo(3));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
		Assert.That(
			schedule[0].Commands[1],
			Is.EqualTo(new SetNoteVolumeCommand(0.5)));
		Assert.That(
			schedule[0].Commands[2],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x0100)));
	}

	[TestCase(0)]
	[TestCase(1)]
	public void SD0AndSD1BothStartOnFirstPostStartTick(byte tick)
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(tick));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(20)));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
	}

	[TestCase(6)]
	[TestCase(15)]
	public void SDAtOrBeyondSpeedSuppressesDelayedNoteForThatRow(byte tick)
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(tick));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
	}

	[Test]
	public void SameRowSpeedChangeControlsCutAndDelayEligibility()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SetSpeedPatternEffect(2));
		cell.Effects.Add(new TrackerNoteCutPatternEffect(3));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(40)));
		Assert.That(
			schedule,
			Has.None.Matches<NoteEvent>(
				e => e.Target.Kind == ChannelTargetKind.Physical));
	}

	[Test]
	public void StartRowSkipsSCxAndSDxCompletely()
	{
		DataPatternDefinition pattern = Pattern(2);

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Note = new StartPatternNote((ObjectId)10U);
		row0.Effects.Add(new TrackerNoteCutPatternEffect(3));
		row0.Effects.Add(new TrackerNoteDelayPatternEffect(2));

		PatternCell row1 = pattern.Grid.GetOrCreateCell(1, 0);
		row1.Note = new StartPatternNote((ObjectId)11U);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(
			((StartNoteCommand)schedule[0].Commands[0]).SourceId,
			Is.EqualTo((ObjectId)11U));
	}

	[Test]
	public void TrackerTickMustFitNibble()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerNoteCutPatternEffect(16));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerNoteDelayPatternEffect(16));
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};
}
