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
		Assert.That(events[1].Target, Is.EqualTo(ChannelTarget.Physical(1)));
		Assert.That(events[2].Target, Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(context.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(timeline.IsComplete, Is.True);
	}

	[TestCase(2.0, 60)]
	[TestCase(0.5, 240)]
	public void FlattenedChildRowsRunAtTheirOwnSpeedWithoutRetimingParent(
		double multiplier, int childSecondRowMilliseconds)
	{
		DataPatternDefinition parent = Pattern(1, 3);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U,
				playbackSpeedMultiplier: multiplier);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		DataPatternDefinition child = Pattern(2, 2);
		child.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes, Has.Length.EqualTo(2));
		Assert.That(notes[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(Math.Min(120,
				childSecondRowMilliseconds))));
		Assert.That(notes[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(Math.Max(120,
				childSecondRowMilliseconds))));
		Assert.That(notes.Single(n => n.Commands.Single() is NoteCutCommand)
			.Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)),
			"Parent's already captured row remains 120 ms.");
	}

	[Test]
	public void AcceleratedFlattenedChildTempoChangesRetimesAllSharedCursors()
	{
		DataPatternDefinition parent = Pattern(1, 3);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U, playbackSpeedMultiplier: 2.0);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		DataPatternDefinition child = Pattern(2, 2);
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		child.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteOff();
		SequencingContext state = new();
		using IncrementalRecursiveTimeline timeline = new(
			state, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(x => x.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(90) }));
		Assert.That(state.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(210)));
	}

	[Test]
	public void AcceleratedFlattenedTrackerTempoSlideUsesScaledSharedTickSpan()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U, playbackSpeedMultiplier: 2.0);
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x11));

		SequencingContext root = new();
		using IncrementalRecursiveTimeline timeline = new(
			root, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand ramp = notes
			.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().Single();
		Assert.That(ramp.TrackerTicks, Is.EqualTo(3.0),
			"A six-tick child row at 2x occupies three shared tracker ticks.");
		Assert.That(ramp.EndingTempo, Is.EqualTo(130.0),
			"The slide still applies five local T11 increments.");
		Assert.That(root.State.Tempo, Is.EqualTo(130.0));
	}

	[Test]
	public void AcceleratedPrivateClockCanAlsoContainIndependentlyScaledFlattenedChild()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U, playbackSpeedMultiplier: 1.5);
		DataPatternDefinition child = Pattern(2, 2);
		child.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();

		SequencingContext privateRoot = new(playbackSpeedMultiplier: 2.0);
		using IncrementalRecursiveTimeline timeline = new(
			privateRoot, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(40)),
			"2x private clock times 1.5x flattened child gives a 3x row rate.");
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FlattenedNestedSequenceComposesPlaybackSpeedMultipliers()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)10U, playbackSpeedMultiplier: 2.0);
		DataSequenceDefinition sequence = new((ObjectId)10U, "Orders");
		sequence.Entries.Add(new SequenceEntry((ObjectId)2U));
		DataPatternDefinition order = Pattern(2, 2);
		order.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)3U, playbackSpeedMultiplier: 2.0);
		DataPatternDefinition leaf = Pattern(3, 2);
		leaf.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, sequence, order, leaf));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(30)),
			"2x Sequence times 2x Pattern composes to 4x leaf rows.");
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void DelayedChildNoteFixedWallOffsetIsNotScaledWithPlaybackSpeed()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U, playbackSpeedMultiplier: 2.0);
		StreamingPattern child = new((ObjectId)2U, 2,
			new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(35), 1.0),
				ChannelTarget.Physical(0), [new NoteOffCommand()]));
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(95)));
	}

	[Test]
	public void FlattenedInitialVolumeBecomesPerVoiceGainWithoutRewritingChildVolume()
	{
		DataPatternDefinition parent = Pattern(1, 3);
		PatternCell flattened = parent.Grid.GetOrCreateCell(0, 0);
		flattened.Note = new StartPatternNote((ObjectId)2U);
		flattened.Volume = 0.5;
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote((ObjectId)90U);
		DataPatternDefinition child = Pattern(2, 2);
		child.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)91U);
		PatternCell explicitChild = child.Grid.GetOrCreateCell(1, 0);
		explicitChild.Note = new StartPatternNote((ObjectId)92U);
		explicitChild.Volume = 0.25;

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		StartNoteCommand[] starts = Drain(timeline)
			.SelectMany(n => n.Commands).OfType<StartNoteCommand>().ToArray();
		Assert.That(starts, Has.Length.EqualTo(3));
		StartNoteCommand inherited = starts.Single(n => n.SourceId == (ObjectId)91U);
		Assert.That(inherited.Volume, Is.Null);
		Assert.That(inherited.GainMultiplier, Is.EqualTo(0.5));
		StartNoteCommand explicitVolume = starts.Single(n => n.SourceId == (ObjectId)92U);
		Assert.That(explicitVolume.Volume, Is.EqualTo(0.25),
			"Child tracker note volume is distinct from enclosing source gain.");
		Assert.That(explicitVolume.GainMultiplier, Is.EqualTo(0.5));
		StartNoteCommand parentNote = starts.Single(n => n.SourceId == (ObjectId)90U);
		Assert.That(parentNote.GainMultiplier, Is.EqualTo(1.0),
			"Flattened gain must never leak to an unrelated parent note.");
	}

	[Test]
	public void NestedFlattenedInitialSourceVolumesMultiplyAcrossInvocations()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		PatternCell parentCall = parent.Grid.GetOrCreateCell(0, 0);
		parentCall.Note = new StartPatternNote((ObjectId)2U);
		parentCall.Volume = 0.5;
		DataPatternDefinition middle = Pattern(2, 1);
		PatternCell middleCall = middle.Grid.GetOrCreateCell(0, 0);
		middleCall.Note = new StartPatternNote((ObjectId)3U);
		middleCall.Volume = 0.4;
		DataPatternDefinition leaf = Pattern(3, 1);
		leaf.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)90U);

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, middle, leaf));
		timeline.AddRoot(parent.Id);
		StartNoteCommand emitted = Drain(timeline)
			.SelectMany(n => n.Commands).OfType<StartNoteCommand>().Single();
		Assert.That(emitted.SourceId, Is.EqualTo((ObjectId)90U));
		Assert.That(emitted.GainMultiplier, Is.EqualTo(0.2).Within(1e-12));
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
	public void ChildFixedWallDeadlineOutlivesFinishedParentAndChildRows()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U);
		StreamingPattern child = new((ObjectId)2U, 1,
			new NoteEvent(
				new MusicalTime(TimeSpan.FromMilliseconds(170), 0),
				ChannelTarget.Physical(0), [new NoteOffCommand()]));

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		long id = timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes, Has.Length.EqualTo(1));
		Assert.That(notes[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(170)));
		Assert.That(timeline.Elapsed,
			Is.EqualTo(TimeSpan.FromMilliseconds(170)));
		Assert.That(timeline.IsInvocationActive(id), Is.False);
		Assert.That(timeline.IsComplete, Is.True);
	}

	[Test]
	public void CancelledSubtreeDiscardsItsDelayedChildWithoutAffectingSibling()
	{
		DataPatternDefinition first = Pattern(1, 1);
		first.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)3U);
		DataPatternDefinition sibling = Pattern(2, 1);
		sibling.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		StreamingPattern child = new((ObjectId)3U, 2,
			new NoteEvent(
				new MusicalTime(TimeSpan.FromMilliseconds(170), 0),
				ChannelTarget.Physical(0), [new NoteOffCommand()]));

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(first, sibling, child));
		long cancelled = timeline.AddRoot(first.Id);
		timeline.AddRoot(sibling.Id);
		Assert.That(timeline.Cancel(cancelled), Is.True);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes, Has.Length.EqualTo(1));
		Assert.That(notes[0].Commands.Single(), Is.TypeOf<NoteCutCommand>());
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
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
	public void FlattenedSiblingVirtualNotesRetainDistinctPatternInvocationScopes()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote((ObjectId)2U);
		StreamingPattern child = new((ObjectId)2U, 1,
			new NoteEvent(new MusicalTime(TimeSpan.Zero, 0.5),
				ChannelTarget.Virtual(7),
				[new StartNoteCommand((ObjectId)90U)]),
			new NoteEvent(new MusicalTime(TimeSpan.Zero, 0.75),
				ChannelTarget.AllVirtualInScope, [new NoteOffCommand()]));

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		List<IncrementalPatternTimelineStep.Emit> actual = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				actual.Add(emit);

		Assert.That(actual, Has.Count.EqualTo(4));
		Assert.That(actual.Select(x => x.Note.Target),
			Is.EqualTo(new[] {
				ChannelTarget.Virtual(7), ChannelTarget.Virtual(7),
				ChannelTarget.AllVirtualInScope, ChannelTarget.AllVirtualInScope,
			}));
		Assert.That(actual[0].InvocationId,
			Is.Not.EqualTo(actual[1].InvocationId));
		Assert.That(actual[0].InvocationId,
			Is.EqualTo(actual[2].InvocationId));
		Assert.That(actual[1].InvocationId,
			Is.EqualTo(actual[3].InvocationId));
		Assert.That(timeline.IsComplete, Is.True);
	}

	[Test]
	public void KnownNestedMixdownPatternRemainsOneRendererOwnedStart()
	{
		StreamingPattern parent = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
				[new StartNoteCommand((ObjectId)2U, Mixdown: true)]));
		StreamingPattern child = new((ObjectId)2U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Virtual(3),
				[new StartNoteCommand((ObjectId)90U)]));

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes, Has.Length.EqualTo(1));
		StartNoteCommand start = notes[0].Commands.OfType<StartNoteCommand>()
			.Single();
		Assert.That(start.Mixdown, Is.True);
		Assert.That(start.SourceId, Is.EqualTo(child.Id));
		Assert.That(notes[0].Target, Is.EqualTo(ChannelTarget.Physical(0)));
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

	[Test]
	public void TwoFlattenedSiblingsKeepDistinctMappedChannelTargets()
	{
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote((ObjectId)2U);
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Select(e => e.Target),
			Is.EqualTo(new[] { ChannelTarget.Physical(0),
				ChannelTarget.Physical(1) }));
		Assert.That(actual.Select(e => e.Offset.TimeOffset),
			Is.All.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void GrandchildPatternBeginsInsideSequenceWithAdditiveChannelMapping()
	{
		DataPatternDefinition root = Pattern(1, 1);
		root.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote((ObjectId)10U);
		DataSequenceDefinition sequence = new((ObjectId)10U, "Nested");
		sequence.Entries.Add(new SequenceEntry((ObjectId)2U));
		DataPatternDefinition intermediate = Pattern(2, 1);
		intermediate.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote((ObjectId)3U);
		DataPatternDefinition grandchild = Pattern(3, 1);
		grandchild.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(
				root, sequence, intermediate, grandchild));
		timeline.AddRoot(root.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Single().Target,
			Is.EqualTo(ChannelTarget.Physical(2)));
		Assert.That(events.Single().Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void MixedParentCommandPreservesNonFlattenedActions()
	{
		StreamingPattern parent = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
				[new SetNoteVolumeCommand(0.5),
					new StartNoteCommand((ObjectId)2U)]));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events, Has.Length.EqualTo(2));
		Assert.That(events[0].Commands.Single(),
			Is.EqualTo(new SetNoteVolumeCommand(0.5)));
		Assert.That(events[1].Commands.Single(), Is.TypeOf<NoteCutCommand>());
	}

	[Test]
	public void MixdownChildStartPassesThroughWithoutFlattening()
	{
		StreamingPattern parent = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
				[new StartNoteCommand((ObjectId)2U, Mixdown: true)]));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] emitted = Drain(timeline);
		Assert.That(emitted, Has.Length.EqualTo(1));
		Assert.That(emitted[0].Commands.OfType<StartNoteCommand>().Single().Mixdown,
			Is.True);
	}

	[Test]
	public void TransposedFlattenedChildEmitsInheritedPitchAndSharesParentTempo()
	{
		StreamingPattern parent = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
				[new StartNoteCommand((ObjectId)2U,
					PitchMultiplier: 2.0)]));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		child.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote((ObjectId)3U);
		SequencingContext root = new();
		using IncrementalRecursiveTimeline timeline = new(
			root, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		StartNoteCommand note = events
			.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>().Single();
		Assert.That(note.SourceId, Is.EqualTo((ObjectId)3U));
		Assert.That(note.PitchMultiplier, Is.EqualTo(2.0),
			"Child sound pitch inherits the recursive invocation transposition.");
		Assert.That(root.State.Tempo, Is.EqualTo(250),
			"Initial pitch must not isolate the flattened shared Tempo clock.");
	}

	[Test]
	public void AdvancingSequenceLoopCanRepeatedlyLaunchFlattenedChildren()
	{
		DataSequenceDefinition sequence = new((ObjectId)10U, "Repeat");
		sequence.Entries.Add(new SequenceEntry((ObjectId)1U));
		DataPatternDefinition parent = Pattern(1, 1);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		int encountered = 0;

		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence, parent, child));
		timeline.AddRoot(sequence.Id, shouldFollowOrderJump: _ => ++encountered < 3);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero,
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240) }));
		Assert.That(encountered, Is.EqualTo(3));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
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

	private sealed class StreamingPattern : PatternDefinition,
		IIncrementalRawPatternNoteGenerator
	{
		private readonly NoteEvent[] _events;

		public StreamingPattern(ObjectId id, int rowCount,
			params NoteEvent[] events) : base(id, "Streaming")
		{
			RowCount = rowCount;
			_events = events;
		}

		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent entry in _events)
				yield return new RawPatternStep.Emit(entry);
		}
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
