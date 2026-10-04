using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PatternLoopEffectTests
{
	[Test]
	public void DataGridTranslatesSBxToRawPatternLoopCommand()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerPatternLoopPatternEffect(0));
		cell.Effects.Add(new TrackerPatternLoopPatternEffect(3));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerPatternLoopCommand(0)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerPatternLoopCommand(3)));
	}

	[Test]
	public void SB0AndSB2RepeatMarkedBlockTwice()
	{
		ObjectId repeatedId = (ObjectId)10U;
		ObjectId afterId = (ObjectId)11U;
		DataPatternDefinition pattern = Pattern(4, 1);

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(repeatedId);
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(2));
		pattern.Grid.GetOrCreateCell(3, 0).Note =
			new StartPatternNote(afterId);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		NoteEvent[] starts = schedule
			.Where(e => e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(1200)));
		Assert.That(starts.Length, Is.EqualTo(4));

		AssertStart(starts[0], repeatedId, 120);
		AssertStart(starts[1], repeatedId, 480);
		AssertStart(starts[2], repeatedId, 840);
		AssertStart(starts[3], afterId, 1080);
	}

	[Test]
	public void SBxWithoutMarkerLoopsFromPatternStart()
	{
		ObjectId repeatedId = (ObjectId)10U;
		ObjectId afterId = (ObjectId)11U;
		DataPatternDefinition pattern = Pattern(3, 1);

		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(repeatedId);
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(afterId);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteEvent[] starts = output.Freeze()
			.Where(e => e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(600)));
		Assert.That(starts.Length, Is.EqualTo(3));
		AssertStart(starts[0], repeatedId, 0);
		AssertStart(starts[1], repeatedId, 240);
		AssertStart(starts[2], afterId, 480);
	}

	[Test]
	public void RepeatedRowsAreResolvedAgainAgainstEvolvedEffectMemory()
	{
		DataPatternDefinition pattern = Pattern(3, 1);

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(new TrackerPatternLoopPatternEffect(0));
		row0.Effects.Add(new TrackerVolumeSlidePatternEffect(0x00));

		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x03));
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule.Any(e =>
				e.Offset.TimeOffset == TimeSpan.Zero
				&& e.Commands.Contains(
					new SetNoteVolumeSlideCommand(-3.0))),
			Is.False);

		Assert.That(
			schedule.Any(e =>
				e.Offset.TimeOffset == TimeSpan.FromMilliseconds(360)
				&& e.Commands.Contains(
					new SetNoteVolumeSlideCommand(-3.0))),
			Is.True);
	}

	[Test]
	public void PatternDelayInsideLoopAppliesOnEveryVisit()
	{
		DataPatternDefinition pattern = Pattern(3, 1);

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(960)));
	}

	[Test]
	public void HigherChannelLoopEndWinsGlobalJumpForThatPass()
	{
		ObjectId rowZeroId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(4, 2);

		PatternCell row0Channel1 = pattern.Grid.GetOrCreateCell(0, 1);
		row0Channel1.Note = new StartPatternNote(rowZeroId);
		row0Channel1.Effects.Add(new TrackerPatternLoopPatternEffect(0));

		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));

		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 1).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent[] rowZeroStarts = output.Freeze()
			.Where(e =>
				e.Commands.OfType<StartNoteCommand>()
					.Any(c => c.SourceId == rowZeroId))
			.ToArray();

		Assert.That(rowZeroStarts.Length, Is.EqualTo(2));
		Assert.That(
			rowZeroStarts[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void StartRowDoesNotExecuteEarlierLoopMarkerOrRows()
	{
		ObjectId skippedId = (ObjectId)10U;
		ObjectId repeatedId = (ObjectId)11U;
		DataPatternDefinition pattern = Pattern(3, 1);

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Note = new StartPatternNote(skippedId);
		row0.Effects.Add(new TrackerPatternLoopPatternEffect(0));

		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(repeatedId);
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out TimeSpan duration);

		NoteEvent[] starts = output.Freeze()
			.Where(e => e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
		Assert.That(starts.Length, Is.EqualTo(2));
		Assert.That(
			starts.SelectMany(e => e.Commands.OfType<StartNoteCommand>())
				.Any(c => c.SourceId == skippedId),
			Is.False);
		AssertStart(starts[0], repeatedId, 0);
		AssertStart(starts[1], repeatedId, 240);
	}

	[Test]
	public void SB0AloneProducesNoResolvedPlaybackCommand()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
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
	public void LoopCountNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerPatternLoopPatternEffect(16));
	}

	private static void AssertStart(
		NoteEvent noteEvent,
		ObjectId expectedId,
		int expectedMilliseconds)
	{
		StartNoteCommand start =
			noteEvent.Commands.OfType<StartNoteCommand>().Single();

		Assert.That(start.SourceId, Is.EqualTo(expectedId));
		Assert.That(
			noteEvent.Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(expectedMilliseconds)));
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
