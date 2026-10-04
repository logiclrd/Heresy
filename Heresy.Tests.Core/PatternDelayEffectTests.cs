using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PatternDelayEffectTests
{
	[Test]
	public void DataGridTranslatesSExToRawPatternDelayCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyTrackerPatternDelayCommand(2)));
	}

	[Test]
	public void SE2MakesOneRowLastThreeRowSpans()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void SE0AddsNoExtraRowSpan()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(0));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FollowingRowStartsAfterDelayedRowCompletes()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(2));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(360)));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(480)));
	}

	[Test]
	public void SameRowSpeedChangeDeterminesDelayedRowSpanLength()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(3));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void RowScopedSlidesRestartAtEachDelayedSpanAndClearAtFinalEnd()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new PitchSlidePatternEffect(48.0));
		cell.Effects.Add(new NoteVolumeSlidePatternEffect(-4.0));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
		Assert.That(schedule.Count, Is.EqualTo(4));

		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new SetPitchSlideCommand(48.0)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new SetNoteVolumeSlideCommand(-4.0)));

		for (int repeat = 1; repeat <= 2; repeat++)
		{
			Assert.That(
				schedule[repeat].Offset.TimeOffset,
				Is.EqualTo(TimeSpan.FromMilliseconds(120 * repeat)));
			Assert.That(
				schedule[repeat].Commands,
				Does.Contain(new SetPitchSlideCommand(48.0)));
			Assert.That(
				schedule[repeat].Commands,
				Does.Contain(new SetNoteVolumeSlideCommand(-4.0)));
		}

		Assert.That(schedule[3].Offset.TimeOffset, Is.EqualTo(duration));
		Assert.That(
			schedule[3].Commands,
			Does.Contain(new ClearPitchSlideCommand()));
		Assert.That(
			schedule[3].Commands,
			Does.Contain(new ClearNoteVolumeSlideCommand()));
	}

	[Test]
	public void RetriggerReceivesTicksAcrossDelayedSpans()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new RetriggerPatternEffect(0x03));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(4));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());

		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			schedule[2].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(
			schedule[3].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));

		for (int i = 1; i < schedule.Count; i++)
		{
			Assert.That(
				schedule[i].Commands[0],
				Is.EqualTo(new RetriggerCurrentVoiceCommand(0)));
		}
	}

	[Test]
	public void DelayedSpanDoesNotRetriggerOriginalNote()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedPatternDelay()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(3));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FirstPatternDelayInChannelOrderWinsForRow()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(1));
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void PatternDelayNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerPatternDelayPatternEffect(16));
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};
}
