using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FinePatternDelayEffectTests
{
	[Test]
	public void DataGridTranslatesS6xToRawFineDelayCommand()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyTrackerFinePatternDelayCommand(3)));
	}

	[Test]
	public void S63ExtendsDefaultRowByThreeTicks()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void FineDelaysOnMultipleChannelsAreSummed()
	{
		DataPatternDefinition pattern = Pattern(1, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(2));
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(220)));
	}

	[Test]
	public void S60AddsNoTicks()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(0));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void SameRowSpeedChangeCombinesWithFineDelay()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(3));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(100)));
	}

	[Test]
	public void FineDelayMultipliesEachPatternDelaySpan()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(320)));
	}

	[Test]
	public void SC8CanFireInsideFineDelayedTicks()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		cell.Effects.Add(new TrackerNoteCutPatternEffect(8));
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
			Is.EqualTo(TimeSpan.FromMilliseconds(160)));
		Assert.That(
			schedule[1].Commands[0],
			Is.TypeOf<NoteCutCommand>());
	}

	[Test]
	public void SD8CanStartInsideFineDelayedTicks()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(8));
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
			Is.EqualTo(TimeSpan.FromMilliseconds(160)));
		Assert.That(
			schedule[0].Commands[0],
			Is.TypeOf<StartNoteCommand>());
	}

	[Test]
	public void SDAtEffectiveRowTickCountStillDoesNotFire()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(9));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
	}

	[Test]
	public void RetriggerUsesFineDelayedTicksWithoutResettingCounter()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new RetriggerPatternEffect(0x03));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent[] retriggers = output.Freeze()
			.Where(e =>
				e.Commands.Any(
					c => c is RetriggerCurrentVoiceCommand))
			.ToArray();

		Assert.That(retriggers.Length, Is.EqualTo(2));
		Assert.That(
			retriggers[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			retriggers[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FineDelayCarriesEffectiveTickCountIntoContinuousSlide()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x01));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands,
			Does.Contain(new SetNoteVolumeSlideCommand(-1.0, 9)));
	}

	[Test]
	public void PatternLoopReappliesFineDelayOnEveryVisit()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(new TrackerPatternLoopPatternEffect(0));
		row0.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		// Two visits to an 8-tick row plus two visits to a normal 6-tick row.
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(560)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedFineDelay()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(5));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(
			output.Freeze()[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void FineDelayNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerFinePatternDelayPatternEffect(16));
	}

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
