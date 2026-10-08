using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalPatternFlowTests
{
	[Test]
	public void SB0SB2RevisitsRowsWithoutExpandingWholePattern()
	{
		DataPatternDefinition pattern = Pattern(4);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(2));
		pattern.Grid.GetOrCreateCell(3, 0).Note =
			new StartPatternNote((ObjectId)11U);

		AssertEagerParity(pattern);
		Observation run = Execute(pattern);
		Assert.That(run.Events.Where(e => e.Commands.OfType<StartNoteCommand>().Any())
			.Select(e => e.Offset.TimeOffset), Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(480),
				TimeSpan.FromMilliseconds(840),
				TimeSpan.FromMilliseconds(1080),
			}));
		Assert.That(run.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(1200)));
		Assert.That(run.Advances, Is.EqualTo(10));
	}

	[Test]
	public void SBxWithoutMarkerRewindsToPatternStartAndResolvesRepeatedMemory()
	{
		DataPatternDefinition pattern = Pattern(3);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x03));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();

		AssertEagerParity(pattern);
		Observation run = Execute(pattern);
		Assert.That(run.Events.Any(e =>
			e.Offset.TimeOffset == TimeSpan.FromMilliseconds(240)
			&& e.Commands.Contains(new SetNoteVolumeSlideCommand(-3))), Is.True);
		Assert.That(run.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(600)));
	}

	[Test]
	public void SBxChannelOrderIsIndependentOfRawGeneratorCreationOrder()
	{
		DataPatternDefinition pattern = Pattern(4, 2);
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)10U);
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 1).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		AssertEagerParity(pattern);
	}

	[Test]
	public void StartRowDoesNotSeedPriorLoopMarkerOrChannelState()
	{
		DataPatternDefinition pattern = Pattern(3);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)11U);
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)12U);
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		AssertEagerParity(pattern, startRow: 1);
		Observation result = Execute(pattern, startRow: 1);
		Assert.That(result.Events.OfType<NoteEvent>()
			.SelectMany(e => e.Commands).OfType<StartNoteCommand>()
			.Select(c => c.SourceId), Is.EqualTo(new[] { (ObjectId)12U, (ObjectId)12U }));
	}

	[Test]
	public void PatternDelayInsideLoopIsAppliedOnEveryVisit()
	{
		DataPatternDefinition pattern = Pattern(3);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(1));
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		AssertEagerParity(pattern);
		Assert.That(Execute(pattern).Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(960)));
	}

	[Test]
	public void BxxAndCxxReturnSequenceFlowOnlyAfterTheirTerminatingRow()
	{
		DataPatternDefinition pattern = Pattern(5, 2);
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(4));
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(7));
		pattern.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();

		Observation result = Execute(pattern);
		Assert.That(result.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(result.Events, Has.Length.EqualTo(1));
		Assert.That(result.Flows, Has.Length.EqualTo(1));
		Assert.That(result.Flows[0].Control,
			Is.EqualTo(new PatternFlowControl(4, 7) { SourceRow = 1 }));
		Assert.That(result.Flows[0].InvocationId, Is.EqualTo(0));
	}

	[Test]
	public void EarlierSequenceControlRowWinsOverLaterRows()
	{
		DataPatternDefinition pattern = Pattern(4);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(3));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternBreakPatternEffect(6));
		Observation result = Execute(pattern);
		Assert.That(result.Flows.Single().Control,
			Is.EqualTo(new PatternFlowControl(3, null) { SourceRow = 0 }));
		Assert.That(result.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void SBxActiveLoopSuppressesSameRowCxxOnHigherPhysicalChannel()
	{
		DataPatternDefinition pattern = Pattern(4, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(8));
		Observation result = Execute(pattern);
		Assert.That(result.Flows.Length, Is.EqualTo(1));
		Assert.That(result.Flows[0].Control.BreakRow, Is.EqualTo(8));
		Assert.That(result.Flows[0].Control.SourceRow, Is.EqualTo(1));
		Assert.That(result.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
	}

	[Test]
	public void RepeatingSBxRowReusesRawGeneratorLazily()
	{
		ReplayableRawSource source = new(
			Event(0, new ApplyTrackerPatternLoopCommand(0)),
			Event(1, new NoteCutCommand()),
			Event(2, new ApplyTrackerPatternLoopCommand(2)));
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(source, 3, context);
		int emitted = 0;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit)
				emitted++;
		Assert.That(emitted, Is.EqualTo(3));
		Assert.That(source.IterationCount, Is.EqualTo(3));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(1080)));
	}

	[Test]
	public void NonReplayableGeneratorCannotRewindForSBx()
	{
		RawSource source = new(
			Event(0, new NoteCutCommand()),
			Event(1, new ApplyTrackerPatternLoopCommand(1)));
		using IncrementalPatternTimeline timeline = new(new SequencingContext());
		SequencingContext context = new();
		using IncrementalPatternTimeline local = new(context);
		local.Add(source, 2, context);
		Assert.Throws<NotSupportedException>(() =>
		{
			while (local.TryStep(out _)) { }
		});
	}

	private static DataPatternDefinition Pattern(int rows, int channels = 1)
		=> new((ObjectId)1U, "Flow") { RowCount = rows, ChannelCount = channels };

	private static NoteEvent Event(double row, NoteCommand command)
		=> new(new MusicalTime(TimeSpan.Zero, row),
			ChannelTarget.Physical(0), [command]);

	private static Observation Execute(DataPatternDefinition pattern, int startRow = 0)
	{
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(pattern, pattern.RowCount, context, startRow);
		List<NoteEvent> notes = [];
		List<IncrementalPatternTimelineStep.Flow> flows = [];
		int advances = 0;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			switch (step)
			{
				case IncrementalPatternTimelineStep.Emit emitted:
					notes.Add(emitted.Note);
					break;
				case IncrementalPatternTimelineStep.Flow flow:
					flows.Add(flow);
					break;
				case IncrementalPatternTimelineStep.Advance:
					advances++;
					break;
			}
		}
		return new Observation(notes.ToArray(), flows.ToArray(),
			timeline.Elapsed, advances);
	}

	private static void AssertEagerParity(DataPatternDefinition pattern, int startRow = 0)
	{
		SequencingContext eagerContext = new();
		NoteScheduleBuilder eager = new();
		PatternNoteProcessor.GenerateNotes(pattern, eagerContext, eager,
			startRow, out TimeSpan eagerDuration, out PatternFlowControl eagerFlow);

		Observation observed = Execute(pattern, startRow);
		NoteEvent[] expected = eager.Freeze().ToArray();
		Assert.That(observed.Events.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < expected.Length; i++)
		{
			Assert.That(observed.Events[i].Commands, Is.EqualTo(expected[i].Commands),
				$"Event {i} diverged. Expected: {string.Join(" | ", expected.Select((e, j) => $"{j}:{e.Offset.TimeOffset}:{string.Join(",", e.Commands)}"))}; Actual: {string.Join(" | ", observed.Events.Select((e, j) => $"{j}:{e.Offset.TimeOffset}:{string.Join(",", e.Commands)}"))}");
			Assert.That(observed.Events[i].Target, Is.EqualTo(expected[i].Target));
			Assert.That(observed.Events[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds).Within(1e-6));
		}
		Assert.That(observed.Elapsed.TotalSeconds,
			Is.EqualTo(eagerDuration.TotalSeconds).Within(1e-6));
		if (eagerFlow.HasControl)
		{
			Assert.That(observed.Flows, Has.Length.EqualTo(1));
			Assert.That(observed.Flows[0].Control, Is.EqualTo(eagerFlow));
		}
		else
		{
			Assert.That(observed.Flows, Is.Empty);
		}
	}

	private sealed record Observation(
		NoteEvent[] Events, IncrementalPatternTimelineStep.Flow[] Flows,
		TimeSpan Elapsed, int Advances);

	private sealed class RawSource(params NoteEvent[] notes)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in notes)
				yield return new RawPatternStep.Emit(note);
		}
	}

	private sealed class ReplayableRawSource(params NoteEvent[] notes)
		: IReplayableRawPatternNoteGenerator
	{
		public int IterationCount { get; private set; }

		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			IterationCount++;
			foreach (NoteEvent note in notes)
				yield return new RawPatternStep.Emit(note);
		}
	}
}
