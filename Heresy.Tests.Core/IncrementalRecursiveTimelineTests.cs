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
public sealed class IncrementalRecursiveTimelineTests
{
	[Test]
	public void FlattenedChildTempoWithinParentRowRetimesLaterParentEvent()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(new SetTempoPatternEffect(250));
		child.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteOff();

		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);

		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.Zero,
				TimeSpan.FromMilliseconds(60) }));
		Assert.That(events.Select(e => e.Commands.Single()),
			Is.EqualTo(new NoteCommand[] {
				new SetTempoCommand(250), new NoteOffCommand(),
				new NoteCutCommand() }));
		Assert.That(events[1].Target, Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(events[2].Target, Is.EqualTo(ChannelTarget.Physical(1)));
		Assert.That(context.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(90)));
		Assert.That(timeline.IsComplete, Is.True);
	}

	[Test]
	public void ChildSequenceBxxRunsOnSameClockAndRetainsItsChannelMemory()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)10U);
		DataSequenceDefinition childSequence = new((ObjectId)10U, "Child");
		childSequence.Entries.Add(new SequenceEntry((ObjectId)2U));
		childSequence.Entries.Add(new SequenceEntry((ObjectId)3U));
		DataPatternDefinition first = Pattern(2, 1);
		first.Grid.GetOrCreateCell(0, 0).SourceId = (ObjectId)90U;
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(1));
		DataPatternDefinition second = Pattern(3, 1);
		second.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(ObjectId.None);

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(),
			new Resolver(parent, childSequence, first, second));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events, Has.Length.EqualTo(1));
		Assert.That(events[0].Commands.OfType<StartNoteCommand>().Single().SourceId,
			Is.EqualTo((ObjectId)90U));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void ChildCanOutliveParentWithoutBlockingParentCompletion()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		DataPatternDefinition child = Pattern(2, 3);
		child.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		long id = timeline.AddRoot(parent.Id);
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e)
				notes.Add(e.Note);
		Assert.That(notes.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
		Assert.That(timeline.IsInvocationActive(id), Is.False);
	}

	[Test]
	public void CancelParentCancelsDescendantsAndPendingWallNotes()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		DataPatternDefinition child = Pattern(2, 4);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		child.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteOff();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		long id = timeline.AddRoot(parent.Id);
		Assert.That(timeline.TryStep(out _), Is.True);
		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(timeline.TryStep(out _), Is.False);
		Assert.That(timeline.IsComplete, Is.True);
	}

	[Test]
	public void CyclicFlattenedSourcesFailBeforeCreatingTheRecursiveChild()
	{
		DataPatternDefinition first = Pattern(1, 1);
		first.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		DataPatternDefinition second = Pattern(2, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)1U);

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(first, second));
		timeline.AddRoot(first.Id);
		var ex = Assert.Throws<InvalidOperationException>(() => Drain(timeline));
		Assert.That(ex!.Message, Does.Contain("cycle"));
	}

	[Test]
	public void UnknownAndMixdownSourcesPassThroughAsRendererOwnedStarts()
	{
		DataPatternDefinition root = Pattern(1, 1);
		root.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)77U);
		DataPatternDefinition child = Pattern(2, 1);

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(root, child));
		timeline.AddRoot(root.Id);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual, Has.Length.EqualTo(1));
		Assert.That(actual[0].Commands.OfType<StartNoteCommand>().Single().SourceId,
			Is.EqualTo((ObjectId)77U));
	}

	[Test]
	public void ADataSequenceRootUsesOneSharedTimelineAndFollowsBxx()
	{
		DataSequenceDefinition root = new((ObjectId)10U, "Root");
		root.Entries.Add(new SequenceEntry((ObjectId)1U));
		root.Entries.Add(new SequenceEntry((ObjectId)2U));
		DataPatternDefinition first = Pattern(1, 1);
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(1));
		DataPatternDefinition second = Pattern(2, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(root, first, second));
		timeline.AddRoot(root.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(timeline.IsComplete, Is.True);
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Pattern")
		{
			RowCount = rows, ChannelCount = 2,
		};

	private static NoteEvent[] Drain(IncrementalRecursiveTimeline timeline)
	{
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				events.Add(emit.Note);
		return events.ToArray();
	}

	private sealed class Resolver(params SongObject[] objects)
		: IIncrementalInvocationResolver
	{
		private readonly Dictionary<ObjectId, SongObject> _objects =
			objects.ToDictionary(o => o.Id);

		public bool TryResolve(ObjectId id, out SongObject? result)
			=> _objects.TryGetValue(id, out result);
	}
}
