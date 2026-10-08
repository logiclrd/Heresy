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
public sealed class IncrementalSequenceCursorTests
{
	[Test]
	public void OrdinaryOrdersAgreeWithEagerSequenceIncludingSharedSourceMemory()
	{
		DataPatternDefinition first = Pattern(1, 2);
		first.Grid.GetOrCreateCell(0, 0).SourceId = (ObjectId)31U;
		first.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(ObjectId.None);
		DataPatternDefinition second = Pattern(2, 2);
		second.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(ObjectId.None);
		second.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		SequenceEntry[] orders = [new(first.Id), new(second.Id)];

		AssertParity(orders, new TestResolver(first, second));
	}

	[Test]
	public void BxxSkipsIntermediateOrderAndCanRevisitPreviousOrder()
	{
		DataPatternDefinition first = Pattern(1, 2);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		first.Grid.GetOrCreateCell(1, 0).Effects.Add(new TrackerOrderJumpPatternEffect(2));
		DataPatternDefinition skipped = Pattern(2, 1);
		skipped.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		DataPatternDefinition last = Pattern(3, 1);
		last.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();

		SequenceEntry[] orders = [new(first.Id), new(skipped.Id), new(last.Id)];
		AssertParity(orders, new TestResolver(first, skipped, last));
	}

	[Test]
	public void CombinedBxxCxxStartsSelectedOrderAtSpecifiedRow()
	{
		DataPatternDefinition first = Pattern(1, 2);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerOrderJumpPatternEffect(2));
		first.Grid.GetOrCreateCell(0, 1).Effects.Add(new TrackerPatternBreakPatternEffect(1));
		DataPatternDefinition middle = Pattern(2, 1);
		middle.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		DataPatternDefinition last = Pattern(3, 3);
		last.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		last.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		last.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();

		SequenceEntry[] orders = [new(first.Id), new(middle.Id), new(last.Id)];
		AssertParity(orders, new TestResolver(first, middle, last));
	}

	[Test]
	public void CxxOverridesNextEntryStartRowOnlyForOneInvocation()
	{
		DataPatternDefinition first = Pattern(1, 1);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerPatternBreakPatternEffect(2));
		DataPatternDefinition target = Pattern(2, 3);
		target.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		target.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		target.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();
		DataPatternDefinition after = Pattern(3, 1);
		after.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequenceEntry[] orders = [new(first.Id), new(target.Id, 1), new(after.Id)];
		AssertParity(orders, new TestResolver(first, target, after));
	}

	[Test]
	public void StartOrderAndStartRowOverrideAgreeWithEagerSequence()
	{
		DataPatternDefinition first = Pattern(1, 1);
		DataPatternDefinition second = Pattern(2, 2);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		second.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		DataPatternDefinition third = Pattern(3, 1);
		third.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		SequenceEntry[] orders = [new(first.Id), new(second.Id), new(third.Id)];
		AssertParity(orders, new TestResolver(first, second, third),
			startOrder: 1, startRow: 1);
	}

	[Test]
	public void MissingPatternsAndZeroLengthPatternsSkipWithoutAdvancingTime()
	{
		DataPatternDefinition empty = Pattern(1, 0);
		DataPatternDefinition last = Pattern(2, 1);
		last.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		SequenceEntry[] orders = [
			new((ObjectId)12345U),
			new(empty.Id),
			new(last.Id),
		];
		AssertParity(orders, new TestResolver(empty, last));
	}

	[Test]
	public void InfiniteB00LoopCanBeSteppedWithoutPreexpansionAndObserverCanStop()
	{
		DataPatternDefinition loop = Pattern(1, 1);
		loop.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		loop.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerOrderJumpPatternEffect(0));
		SequenceEntry[] orders = [new(loop.Id)];
		List<SequenceOrderJumpEncounter> encounters = [];
		SequencingContext context = new();
		using IncrementalSequenceCursor cursor = new(
			orders, new TestResolver(loop), context,
			shouldFollowOrderJump: encounter =>
			{
				encounters.Add(encounter);
				return encounters.Count < 4;
			});
		NoteEvent[] actual = Drain(cursor);
		Assert.That(actual.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240),
				TimeSpan.FromMilliseconds(360),
			}));
		Assert.That(encounters, Has.Count.EqualTo(4));
		Assert.That(encounters, Is.All.EqualTo(new SequenceOrderJumpEncounter(loop.Id, 0, 0)));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
		Assert.That(cursor.IsComplete, Is.True);
	}

	[Test]
	public void FlowStepIsReportedBeforeTheNextOrderIsInstantiated()
	{
		DataPatternDefinition first = Pattern(1, 1);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerOrderJumpPatternEffect(1));
		DataPatternDefinition target = Pattern(2, 1);
		target.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		using IncrementalSequenceCursor cursor = new(
			[new(first.Id), new(target.Id)], new TestResolver(first, target),
			new SequencingContext());

		IncrementalPatternTimelineStep.Flow? flow = null;
		while (cursor.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Flow f)
			{
				flow = f;
				break;
			}
		}
		Assert.That(flow, Is.Not.Null);
		Assert.That(flow!.Control, Is.EqualTo(new PatternFlowControl(1, null)
			{ SourceRow = 0 }));
		Assert.That(cursor.Order, Is.EqualTo(0));
		Assert.That(Drain(cursor), Has.Length.EqualTo(1));
		Assert.That(cursor.Order, Is.EqualTo(2));
	}

	[Test]
	public void TimingChangesAndQ00MemorySurviveARevisitedOrder()
	{
		DataPatternDefinition first = Pattern(1, 2);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0xFA));
		first.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x03));
		DataPatternDefinition second = Pattern(2, 1);
		second.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0));
		SequenceEntry[] orders = [new(first.Id), new(second.Id)];
		AssertParity(orders, new TestResolver(first, second));
	}

	[Test]
	public void UnsupportedScriptPatternFailsInsteadOfFallingBackToEagerExpansion()
	{
		IRawPatternNoteGenerator generator = new NonStreamingPattern();
		using IncrementalSequenceCursor cursor = new(
			[new((ObjectId)4U)], new SingleResolver(generator),
			new SequencingContext());
		Assert.Throws<NotSupportedException>(() => cursor.TryStep(out _));
	}

	[Test]
	public void EntryVisitsRequireProgressOrFailWithExplicitBudget()
	{
		// No pattern invocation is required here: a large run of missing
		// entries cannot starve a cooperative caller indefinitely.
		SequenceEntry[] missing = Enumerable.Range(1, 9000)
			.Select(i => new SequenceEntry((ObjectId)(uint)i)).ToArray();
		using IncrementalSequenceCursor cursor = new(
			missing, new TestResolver(), new SequencingContext());
		Assert.Throws<InvalidOperationException>(() => cursor.TryStep(out _));
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Order") { RowCount = rows, ChannelCount = 2 };

	private static void AssertParity(
		IReadOnlyList<SequenceEntry> entries, TestResolver resolver,
		int startOrder = 0, int? startRow = null)
	{
		SequencingContext eagerContext = new();
		NoteScheduleBuilder eager = new();
		SequenceNoteProcessor.GenerateNotes(entries, resolver,
			eagerContext, eager, startOrder, startRow, out TimeSpan duration);
		SequencingContext lazyContext = new();
		using IncrementalSequenceCursor cursor = new(
			entries, resolver, lazyContext, startOrder, startRow);
		NoteEvent[] actual = Drain(cursor);
		NoteEvent[] expected = eager.Freeze().ToArray();
		Assert.That(actual.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < expected.Length; i++)
		{
			Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds).Within(1e-6));
			Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target));
			Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands));
		}
		Assert.That(cursor.Elapsed.TotalSeconds,
			Is.EqualTo(duration.TotalSeconds).Within(1e-6));
		Assert.That(lazyContext.State.Tempo, Is.EqualTo(eagerContext.State.Tempo));
		Assert.That(lazyContext.State.Speed, Is.EqualTo(eagerContext.State.Speed));
	}

	private static NoteEvent[] Drain(IncrementalSequenceCursor cursor)
	{
		List<NoteEvent> notes = [];
		while (cursor.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e)
				notes.Add(e.Note);
		return notes.ToArray();
	}

	private sealed class TestResolver(params DataPatternDefinition[] patterns)
		: ISequencePatternResolver
	{
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator> _patterns =
			patterns.ToDictionary(p => p.Id, p => (IRawPatternNoteGenerator)p);

		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? pattern)
			=> _patterns.TryGetValue(id, out pattern);
	}

	private sealed class SingleResolver(IRawPatternNoteGenerator pattern)
		: ISequencePatternResolver
	{
		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? output)
		{
			output = pattern;
			return true;
		}
	}

	private sealed class NonStreamingPattern : IRawPatternNoteGenerator
	{
		public void GenerateRawNotes(SequencingContext context,
			INoteReceiver output, out double rowCount)
			=> rowCount = 1;
	}
}
