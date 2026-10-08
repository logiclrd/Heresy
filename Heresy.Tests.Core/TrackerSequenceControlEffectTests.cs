using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerSequenceControlEffectTests
{
	[Test]
	public void DataGridTranslatesBxxAndCxxToSequencingControlCommands()
	{
		DataPatternDefinition pattern = Pattern((ObjectId)1U, 1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerOrderJumpPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternBreakPatternEffect(0x34));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToArray();

		Assert.That(
			commands,
			Does.Contain(new ApplyTrackerOrderJumpCommand(0x12)));
		Assert.That(
			commands,
			Does.Contain(new ApplyTrackerPatternBreakCommand(0x34)));
	}

	[Test]
	public void BxxEndsPatternAfterItsRowAndReportsOrderJump()
	{
		ObjectId skippedSource = (ObjectId)20U;
		DataPatternDefinition pattern = Pattern((ObjectId)1U, 3, 1);
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(3));
		pattern.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(skippedSource);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 0,
			out TimeSpan duration,
			out PatternFlowControl flowControl);

		Assert.That(flowControl.OrderJump, Is.EqualTo(3));
		Assert.That(flowControl.BreakRow, Is.Null);
		Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(0.24)));
		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<StartNoteCommand>(),
			Is.Empty);
	}

	[Test]
	public void BxxAndCxxOnSameRowComposeOrderAndBreakRow()
	{
		DataPatternDefinition pattern = Pattern((ObjectId)1U, 2, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(4));
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(7));

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			new NoteScheduleBuilder(),
			startRow: 0,
			out TimeSpan duration,
			out PatternFlowControl flowControl);

		Assert.That(flowControl.OrderJump, Is.EqualTo(4));
		Assert.That(flowControl.BreakRow, Is.EqualTo(7));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(0.12)));
	}

	[Test]
	public void CxxIsIgnoredWhileEarlierChannelSBxIsActivelyLooping()
	{
		ObjectId afterLoopSource = (ObjectId)20U;
		DataPatternDefinition pattern = Pattern((ObjectId)1U, 3, 2);

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(2));
		pattern.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(afterLoopSource);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 0,
			out TimeSpan duration,
			out PatternFlowControl flowControl);

		Assert.That(flowControl.OrderJump, Is.Null);
		Assert.That(flowControl.BreakRow, Is.EqualTo(2));
		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(480)));
		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<StartNoteCommand>(),
			Is.Empty);
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedSequenceControl()
	{
		DataPatternDefinition pattern = Pattern((ObjectId)1U, 2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(4));

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			new NoteScheduleBuilder(),
			startRow: 1,
			out _,
			out PatternFlowControl flowControl);

		Assert.That(flowControl.HasControl, Is.False);
	}

	[Test]
	public void SequenceProcessorFollowsBxxOrderJump()
	{
		ObjectId firstPattern = (ObjectId)1U;
		ObjectId skippedPattern = (ObjectId)2U;
		ObjectId targetPattern = (ObjectId)3U;
		ObjectId skippedSource = (ObjectId)20U;
		ObjectId targetSource = (ObjectId)30U;

		DataPatternDefinition first = Pattern(firstPattern, 1, 1);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(2));

		DataPatternDefinition skipped =
			Pattern(skippedPattern, 1, 1);
		skipped.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(skippedSource);

		DataPatternDefinition target =
			Pattern(targetPattern, 1, 1);
		target.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(targetSource);

		DataSequenceDefinition sequence =
			new((ObjectId)100U, "Sequence");
		sequence.Entries.Add(new SequenceEntry(firstPattern));
		sequence.Entries.Add(new SequenceEntry(skippedPattern));
		sequence.Entries.Add(new SequenceEntry(targetPattern));

		NoteScheduleBuilder output = new();
		SequenceNoteProcessor.GenerateNotes(
			sequence,
			new TestPatternResolver(first, skipped, target),
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		StartNoteCommand[] starts =
			schedule
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<StartNoteCommand>()
				.ToArray();

		Assert.That(
			starts,
			Is.EqualTo(
				new[]
				{
					new StartNoteCommand(targetSource),
				}));
		Assert.That(
			schedule.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromSeconds(0.12)));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(0.24)));
	}

	[Test]
	public void OrderJumpObserverCanTerminateThirdEncounterWithSameBxx()
	{
		ObjectId patternId = (ObjectId)1U;
		DataPatternDefinition pattern =
			Pattern(
				patternId,
				rows: 1,
				channels: 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));

		DataSequenceDefinition sequence =
			new((ObjectId)100U, "Loop");
		sequence.Entries.Add(
			new SequenceEntry(patternId));

		List<SequenceOrderJumpEncounter> encounters = [];
		NoteScheduleBuilder output = new();
		SequenceNoteProcessor.GenerateNotes(
			sequence.Entries,
			new TestPatternResolver(pattern),
			new SequencingContext(),
			output,
			startOrder: 0,
			startRow: null,
			out TimeSpan duration,
			rowStarted: null,
			shouldFollowOrderJump: encounter =>
			{
				encounters.Add(encounter);
				return encounters.Count < 3;
			});

		Assert.That(encounters, Has.Count.EqualTo(3));
		Assert.That(
			encounters,
			Has.All.EqualTo(
				new SequenceOrderJumpEncounter(
					patternId,
					PatternRow: 0,
					TargetOrder: 0)));
		Assert.That(
			duration,
			Is.EqualTo(
				TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void CxxStartsNextSequenceEntryAtRequestedRowWithoutExecutingSkippedRows()
	{
		ObjectId firstPattern = (ObjectId)1U;
		ObjectId nextPattern = (ObjectId)2U;
		ObjectId targetSource = (ObjectId)30U;

		DataPatternDefinition first = Pattern(firstPattern, 1, 1);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternBreakPatternEffect(2));

		DataPatternDefinition next = Pattern(nextPattern, 4, 1);
		next.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetSpeedPatternEffect(3));
		next.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(targetSource);

		DataSequenceDefinition sequence =
			new((ObjectId)100U, "Sequence");
		sequence.Entries.Add(new SequenceEntry(firstPattern));
		sequence.Entries.Add(new SequenceEntry(nextPattern));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();
		SequenceNoteProcessor.GenerateNotes(
			sequence,
			new TestPatternResolver(first, next),
			context,
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<StartNoteCommand>()
				.Single(),
			Is.EqualTo(new StartNoteCommand(targetSource)));
		Assert.That(
			schedule.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromSeconds(0.12)));
		Assert.That(context.State.Speed, Is.EqualTo(6));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(0.36)));
	}

	[Test]
	public void CombinedBxxCxxStartsJumpTargetAtRequestedRow()
	{
		ObjectId firstPattern = (ObjectId)1U;
		ObjectId skippedPattern = (ObjectId)2U;
		ObjectId targetPattern = (ObjectId)3U;
		ObjectId skippedSource = (ObjectId)20U;
		ObjectId targetSource = (ObjectId)30U;

		DataPatternDefinition first = Pattern(firstPattern, 1, 2);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(2));
		first.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(1));

		DataPatternDefinition skipped =
			Pattern(skippedPattern, 1, 1);
		skipped.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(skippedSource);

		DataPatternDefinition target =
			Pattern(targetPattern, 3, 1);
		target.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(skippedSource);
		target.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(targetSource);

		DataSequenceDefinition sequence =
			new((ObjectId)100U, "Sequence");
		sequence.Entries.Add(new SequenceEntry(firstPattern));
		sequence.Entries.Add(new SequenceEntry(skippedPattern));
		sequence.Entries.Add(new SequenceEntry(targetPattern));

		NoteScheduleBuilder output = new();
		SequenceNoteProcessor.GenerateNotes(
			sequence,
			new TestPatternResolver(first, skipped, target),
			new SequencingContext(),
			output,
			out _);

		StartNoteCommand[] starts =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<StartNoteCommand>()
				.ToArray();

		Assert.That(
			starts,
			Is.EqualTo(
				new[]
				{
					new StartNoteCommand(targetSource),
				}));
	}

	private static DataPatternDefinition Pattern(
		ObjectId id,
		int rows,
		int channels)
		=> new(id, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};

	private sealed class TestPatternResolver :
		ISequencePatternResolver
	{
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator>
			_patterns = [];

		public TestPatternResolver(
			params IRawPatternNoteGenerator[] patterns)
		{
			foreach (IRawPatternNoteGenerator pattern in patterns)
			{
				if (pattern is not PatternDefinition definition)
				throw new ArgumentException(
					"Test patterns must expose an object ID.");

				_patterns.Add(definition.Id, pattern);
			}
		}

		public bool TryResolve(
			ObjectId patternId,
			out IRawPatternNoteGenerator? pattern)
			=> _patterns.TryGetValue(patternId, out pattern);
	}
}
