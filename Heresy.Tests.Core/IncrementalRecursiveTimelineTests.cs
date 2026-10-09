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
		Assert.That(events[0].Commands.Single(), Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(events[1].Commands.Single(), Is.TypeOf<NoteOffCommand>());
		Assert.That(events[2].Commands.Single(),
			Is.EqualTo(new ControlFlattenedSourceCommand(
				1, NoteDisplacementAction.Cut)));
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
		// The unrelated parent host is only a timing marker.
		// Cutting the instigating channel would terminate the child.
		parent.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
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
		flattened.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
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
		Assert.That(inherited.GainMultiplier, Is.EqualTo(1.0));
		Assert.That(inherited.ParentSourceScopes, Has.Count.EqualTo(1),
			"Child notes retain a live reference to their source-volume scope.");
		StartNoteCommand explicitVolume = starts.Single(n => n.SourceId == (ObjectId)92U);
		Assert.That(explicitVolume.Volume, Is.EqualTo(0.25),
			"Child tracker note volume is distinct from enclosing source gain.");
		Assert.That(explicitVolume.GainMultiplier, Is.EqualTo(1.0));
		Assert.That(explicitVolume.ParentSourceScopes,
			Is.EqualTo(inherited.ParentSourceScopes));
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
		Assert.That(emitted.GainMultiplier, Is.EqualTo(1.0));
		Assert.That(emitted.ParentSourceScopes, Has.Count.EqualTo(2));
		Assert.That(emitted.ParentSourceScopes![0],
			Is.LessThan(emitted.ParentSourceScopes[1]),
			"Each nested instigator contributes an independent live note-volume factor.");
	}

	[Test]
	public void FlattenedStartCommandsCarryUniqueControllerAndOriginalNoteVolume()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.Note = new StartPatternNote((ObjectId)2U);
		call.Volume = 0.6;
		call.Effects.Add(new TrackerVolumeSlidePatternEffect(0x01));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote((ObjectId)90U);
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = DrainRaw(timeline);
		BeginFlattenedSourceVolumeCommand begin = events
			.SelectMany(e => e.Commands).OfType<BeginFlattenedSourceVolumeCommand>().Single();
		Assert.That(begin.ChildScopeId, Is.GreaterThan(0));
		Assert.That(begin.InitialVolume, Is.EqualTo(0.6));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetNoteVolumeSlideCommand>(), Is.Not.Empty,
			"Dxx on an instigator must retain the source's volume slide.");
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.Zero);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>().Single().ParentSourceScopes,
			Is.EqualTo(new[] { begin.ChildScopeId }));
	}

	[Test]
	public void FlattenedChildGetsIndependentSourceAndEffectMemoryAtOverlappingHost()
	{
		DataPatternDefinition parent = Pattern(1, 3);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote((ObjectId)92U);
		parent.Grid.GetOrCreateCell(2, 0).Note = new StartPatternNote();
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).SourceId = (ObjectId)93U;
		child.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();

		SequencingContext context = new();
		context.GetPhysicalChannelState(0).ResolveEffectParameter(
			EffectMemorySlot.Retrigger, 0x12);
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		StartNoteCommand[] starts = Drain(timeline)
			.SelectMany(e => e.Commands).OfType<StartNoteCommand>().ToArray();
		Assert.That(starts.Select(n => n.SourceId),
			Is.EqualTo(new[] { (ObjectId)93U, (ObjectId)92U, (ObjectId)92U }),
			"Child Source choice must not change the parent's remembered Source.");
		Assert.That(context.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo((ObjectId)92U));
		Assert.That(context.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Retrigger, 0),
			Is.EqualTo(0x12));
	}

	[Test]
	public void TwoFlatInvocationsHaveDifferentLocalEffectMemoryDespiteSameHost()
	{
		SequencingContext root = new();
		SequencingContext first = root.FlattenedChild(physicalChannelOffset: 2);
		SequencingContext second = root.FlattenedChild(physicalChannelOffset: 2);
		first.GetPhysicalChannelState(0).CurrentSourceId = (ObjectId)80U;
		first.GetPhysicalChannelState(0).ResolveEffectParameter(
			EffectMemorySlot.Retrigger, 0x15);
		Assert.That(second.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo(ObjectId.None));
		Assert.That(second.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Retrigger, 0), Is.Zero);
		Assert.That(root.GetPhysicalChannelState(2).CurrentSourceId,
			Is.EqualTo(ObjectId.None));
		Assert.That(ReferenceEquals(first.State, root.State), Is.True,
			"Tempo and Speed still belong to one shared clock.");
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
		Assert.That(notes.Single(n => n.Commands.Any(c =>
			c is not BeginFlattenedSourceVolumeCommand))
			.Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
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

		actual = actual.Where(x => x.Note.Commands.Any(c =>
			c is not BeginFlattenedSourceVolumeCommand)).ToList();
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
		PatternCell instigator = parent.Grid.GetOrCreateCell(0, 0);
		instigator.Note = new StartPatternNote((ObjectId)2U);
		instigator.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
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

	[Test]
	public void RepeatedNestedSourcesReclaimScopeMemoryWithoutReusingIds()
	{
		DataSequenceDefinition sequence = new((ObjectId)10U, "Loop");
		sequence.Entries.Add(new SequenceEntry((ObjectId)1U));
		DataPatternDefinition outer = Pattern(1, 1);
		outer.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		outer.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		DataPatternDefinition middle = Pattern(2, 1);
		middle.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)3U);
		DataPatternDefinition inner = Pattern(3, 1);
		inner.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		SequencingContext root = new();
		int jumps = 0;
		using IncrementalRecursiveTimeline timeline = new(
			root, new Resolver(sequence, outer, middle, inner));
		timeline.AddRoot(sequence.Id,
			shouldFollowOrderJump: _ => ++jumps < 1024);
		HashSet<long> emittedScopes = [];
		int peak = 0;
		int emitted = 0;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			peak = Math.Max(peak, root.ScopedMemory.ActiveScopeCount);
			if (step is IncrementalPatternTimelineStep.Emit e
				&& e.Note.Commands.Any(c => c is NoteCutCommand))
			{
				Assert.That(e.Note.PhysicalPlaybackOwner, Is.GreaterThan(0));
				emittedScopes.Add(e.Note.PhysicalPlaybackOwner);
				emitted++;
			}
		}
		Assert.That(jumps, Is.EqualTo(1024));
		Assert.That(emitted, Is.EqualTo(1024));
		Assert.That(emittedScopes.Count, Is.EqualTo(1024),
			"Every invocation must receive a fresh, non-reused scope ID.");
		Assert.That(peak, Is.LessThanOrEqualTo(4),
			"Scope memory growth must track active recursion, not loop iterations.");
		Assert.That(root.ScopedMemory.ActiveScopeCount, Is.Zero);
		Assert.That(root.ScopedMemory.MaterializedScopeCount, Is.Zero);
	}

	[Test]
	public void CancelNestedFlatteningReleasesOnlyThatSubtreeScopes()
	{
		DataPatternDefinition outer = Pattern(1, 3);
		outer.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		DataPatternDefinition middle = Pattern(2, 4);
		middle.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)3U);
		DataPatternDefinition leaf = Pattern(3, 4);
		SequencingContext root = new();
		using IncrementalRecursiveTimeline timeline = new(
			root, new Resolver(outer, middle, leaf));
		timeline.AddRoot(outer.Id);
		Assert.That(timeline.TryStep(out _), Is.True);
		Assert.That(root.ScopedMemory.ActiveScopeCount,
			Is.GreaterThanOrEqualTo(1));
		timeline.Dispose();
		Assert.That(root.ScopedMemory.ActiveScopeCount, Is.Zero);
		Assert.That(root.ScopedMemory.MaterializedScopeCount, Is.Zero);
	}

	[Test]
	public void RawFlatteningStartIgnoresVoiceEffectsWithoutContaminatingCallerMemory()
	{
		StreamingPattern root = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
			[
				new StartNoteCommand((ObjectId)2U, Volume: 0.6),
				new ApplyRetriggerCommand(0xA3),
				new ApplySampleOffsetCommand(0x17),
				new ApplyTrackerGlissandoControlCommand(1),
				new ApplyTrackerTempoCommand(0xC8),
			]));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(root, child));
		timeline.AddRoot(root.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<NoteOffCommand>().Count(), Is.EqualTo(1));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<RetriggerCurrentVoiceCommand>(), Is.Empty);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetSourceFrameOffsetCommand>(), Is.Empty);
		Assert.That(context.State.Tempo, Is.EqualTo(200));
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Retrigger, out _), Is.False);
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.SampleOffset, out _), Is.False);
		Assert.That(context.GetPhysicalChannelState(0).GlissandoEnabled, Is.False);
		Assert.That(context.GetPhysicalChannelState(0).NoteVolume,
			Is.EqualTo(0.6));
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(3));
		Assert.That(context.Diagnostics.Drain()
			.Count(d => d.Code == "HRSEQ003"), Is.EqualTo(3));
	}

	[Test]
	public void DataFlatteningStartDoesNotBecomePortamentoTarget()
	{
		DataPatternDefinition root = Pattern(1, 1);
		PatternCell cell = root.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)2U);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x05));
		cell.Effects.Add(new RetriggerPatternEffect(0xA3));
		cell.Effects.Add(new TrackerTempoPatternEffect(0x80));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(root, child));
		timeline.AddRoot(root.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<NoteOffCommand>().Count(), Is.EqualTo(1),
			"The Pattern still starts, even with Gxx in its stored cell.");
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.TonePortamento, out _),
			Is.False);
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Retrigger, out _),
			Is.False);
		Assert.That(context.State.Tempo, Is.EqualTo(128));
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(2));
		Assert.That(cell.Effects, Has.Count.EqualTo(3),
			"Playback filtering must never mutate stored Pattern effects.");
	}

	[Test]
	public void FlattenedStartAppliesNoteVolumePortionsOfCombinedEffectsButIgnoresPitchParts()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.Note = new StartPatternNote((ObjectId)2U);
		call.Volume = 0.6;
		call.Effects.Add(new TrackerVolumeSlidePatternEffect(0x21));
		call.Effects.Add(new VibratoVolumeSlidePatternEffect(0x32));
		call.Effects.Add(new TonePortamentoVolumeSlidePatternEffect(0x43));
		call.Effects.Add(new TrackerVolumeColumnPatternEffect(
			TrackerVolumeColumnEffectKind.VolumeSlideUp, 5));
		call.Effects.Add(new TrackerChannelVolumePatternEffect(32));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands).OfType<NoteOffCommand>()
			.Count(), Is.EqualTo(1),
			"Gxx+volume-slide must not turn the flattened start into a pitch target.");
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetNoteVolumeSlideCommand>(), Is.Not.Empty,
			"Dxx, Kxx, Lxx and volume-column slides must control the instigating source.");
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<AdjustCurrentNoteVolumeCommand>(), Is.Empty);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetOverallChannelVolumeCommand>().Select(v => v.Volume),
			Is.EqualTo(new[] { 0.5 }),
			"Mxx is a true channel-wide control and must survive filtering.");
		foreach (EffectMemorySlot slot in new[] {
			EffectMemorySlot.VolumeSlide, EffectMemorySlot.VolumeColumnSlide })
			Assert.That(context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(slot, out _), Is.True,
				$"Source-volume effect must remember its {slot} parameter.");
		foreach (EffectMemorySlot slot in new[] {
			EffectMemorySlot.TonePortamento, EffectMemorySlot.Vibrato })
			Assert.That(context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(slot, out _), Is.False,
				$"Ignored pitch effect must not seed {slot} memory.");
		Assert.That(context.GetPhysicalChannelState(0).NoteVolume,
			Is.EqualTo(0.6), "Caller still remembers the explicit start volume.");
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(2));
		Assert.That(call.Effects, Has.Count.EqualTo(5));
	}

	[Test]
	public void LaterRowCombinedEffectsControlActiveSourceVolumeWithoutPitchMemory()
	{
		DataPatternDefinition parent = Pattern(1, 3);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new VibratoVolumeSlidePatternEffect(0x01));
		parent.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TonePortamentoVolumeSlidePatternEffect(0x01));
		DataPatternDefinition child = Pattern(2, 3);
		child.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)90U);
		SequencingContext root = new();
		using IncrementalRecursiveTimeline timeline = new(
			root, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetNoteVolumeSlideCommand>().Count(),
			Is.EqualTo(2), "Both Kxx and Lxx preserve their source-volume slide.");
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.VolumeSlide, out _),
			Is.True);
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Vibrato, out _),
			Is.False);
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.TonePortamento, out _),
			Is.False);
		Assert.That(root.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(2));
	}

	[Test]
	public void OmittedSourceStartCanFlattenDespitePortamentoAndVolumeEffects()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).SourceId = (ObjectId)2U;
		PatternCell call = parent.Grid.GetOrCreateCell(1, 0);
		call.Note = new StartPatternNote();
		call.Effects.Add(new TonePortamentoPatternEffect(0x05));
		call.Effects.Add(new TonePortamentoVolumeSlidePatternEffect(0x35));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands).OfType<NoteOffCommand>()
			.Count(), Is.EqualTo(1));
		Assert.That(events.Any(e => e.PhysicalPlaybackOwner > 0), Is.True);
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(2));
	}

	[Test]
	public void RawScriptedFlatteningKeepsCombinedVolumeSlideWithoutPitchPart()
	{
		StreamingPattern parent = new((ObjectId)1U, 1,
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(0),
			[
				new StartNoteCommand((ObjectId)2U),
				new ApplyVibratoVolumeSlideCommand(0x32),
				new ApplyTonePortamentoVolumeSlideCommand(0x43),
				new ApplyTrackerChannelVolumeCommand(32),
			]));
		DataPatternDefinition child = Pattern(2, 1);
		child.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands).OfType<NoteOffCommand>()
			.Count(), Is.EqualTo(1));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetOverallChannelVolumeCommand>().Select(v => v.Volume),
			Is.EqualTo(new[] { 0.5 }));
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.VolumeSlide, out _), Is.True);
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.ChannelVolumeSlide, out _), Is.False);
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(2));
	}

	[Test]
	public void RawFilteringKeepsCommandOrderingAfterConsecutiveUnsupportedEffects()
	{
		SequencingContext context = new()
		{
			IsFlattenedSource = source => source == (ObjectId)2U,
		};
		NoteEvent raw = new(MusicalTime.Zero, ChannelTarget.Physical(0),
			[
				new SetNoteVolumeCommand(0.8),
				new ApplyVolumeSlideCommand(0x34),
				new ApplyVibratoVolumeSlideCommand(0x12),
				new SetOverallChannelVolumeCommand(0.5),
				new StartNoteCommand((ObjectId)2U),
				new ApplyTrackerChannelVolumeCommand(32),
			]);
		NoteEvent filtered = FlattenedSourceEffectPolicy.Filter(raw, context);
		Assert.That(filtered.Commands, Is.EqualTo(new NoteCommand[]
		{
			new SetNoteVolumeCommand(0.8),
			new ApplyVolumeSlideCommand(0x34),
			new ApplyVolumeSlideCommand(0x12),
			new SetOverallChannelVolumeCommand(0.5),
			new StartNoteCommand((ObjectId)2U),
			new ApplyTrackerChannelVolumeCommand(32),
		}));
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(1));
	}

	[Test]
	public void MixdownAndOrdinaryStartsDoNotTriggerFlattenedWarnings()
	{
		SequencingContext context = new();
		context.IsFlattenedSource = id => id == (ObjectId)2U;
		NoteEvent mixdown = new(MusicalTime.Zero, ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)2U, Mixdown: true),
				new ApplyRetriggerCommand(0xA3)]);
		NoteEvent ordinary = new(MusicalTime.Zero, ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)99U),
				new ApplySampleOffsetCommand(0x17)]);
		Assert.That(FlattenedSourceEffectPolicy.Filter(mixdown, context),
			Is.SameAs(mixdown));
		Assert.That(FlattenedSourceEffectPolicy.Filter(ordinary, context),
			Is.SameAs(ordinary));
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.Zero);
	}

	[Test]
	public void IgnoredFlattenedEffectsUseIndependentBoundedDiagnosticQueue()
	{
		SequencingContext context = new();
		const int excess = 1000;
		for (int i = 0;
			i < Heresy.Core.Diagnostics.SequencingDiagnosticLog.MaximumIndividualMessages
				+ excess; i++)
			context.Diagnostics.ReportIgnoredFlatteningEffect("Retrigger", 2);
		var messages = context.Diagnostics.Drain();
		Assert.That(messages.Count(x => x.Code == "HRSEQ003"),
			Is.EqualTo(Heresy.Core.Diagnostics.SequencingDiagnosticLog.MaximumIndividualMessages));
		Assert.That(messages.Count(x => x.Code == "HRSEQ004"), Is.EqualTo(1));
		Assert.That(context.Diagnostics.Drain(), Is.Empty);
	}

	[Test]
	public void DeferredSourcePortamentoReinterpretsPatternStartAtResolutionTime()
	{
		SequencingContext context = new()
		{
			ResolvePatternSourcesAtRowTime = true,
			IsFlattenedSource = source => source == (ObjectId)2U,
		};
		NoteEvent unresolved = new(
			MusicalTime.Zero, ChannelTarget.Physical(0),
			[new ApplyTonePortamentoCommand(5,
				new StartNoteCommand(ObjectId.None))]);
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			new SourceChangingDeferredGenerator(unresolved, (ObjectId)2U),
			context, output, out _);
		NoteEvent[] resolved = output.Freeze().ToArray();
		Assert.That(resolved.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>().Select(e => e.SourceId),
			Is.EqualTo(new[] { (ObjectId)2U }),
			"An omitted Source is judged when it actually resolves, not at generator lookahead.");
		Assert.That(resolved.SelectMany(e => e.Commands)
			.OfType<SetTonePortamentoCommand>(), Is.Empty);
		Assert.That(context.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.TonePortamento, out _),
			Is.False);
		Assert.That(context.Diagnostics.IgnoredFlatteningEffects, Is.EqualTo(1));
	}

	private sealed class SourceChangingDeferredGenerator(
		NoteEvent eventBeforeSourceResolution, ObjectId resolvedSource)
		: IDeferredSourcePatternGenerator
	{
		public void GenerateRawNotes(SequencingContext context,
			INoteReceiver output, out double rowCount)
		{
			output.Append(eventBeforeSourceResolution);
			// Mimics source selection becoming authoritative only after
			// a different coroutine updates the shared logical context.
			context.GetPhysicalChannelState(0).CurrentSourceId = resolvedSource;
			rowCount = 1;
		}
	}

	[Test]
	public void InstigatorCutCancelsOnlyItsFlattenedSubtreeNotSiblingOrHost()
	{
		DataPatternDefinition parent = Pattern(1, 4);
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote((ObjectId)3U);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		DataPatternDefinition first = Pattern(2, 3);
		first.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();
		DataPatternDefinition sibling = Pattern(3, 3);
		sibling.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, first, sibling));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		ControlFlattenedSourceCommand[] controls = notes
			.SelectMany(n => n.Commands)
			.OfType<ControlFlattenedSourceCommand>().ToArray();
		Assert.That(controls, Has.Length.EqualTo(1));
		Assert.That(controls[0].Action, Is.EqualTo(NoteDisplacementAction.Cut));
		Assert.That(notes.SelectMany(n => n.Commands).OfType<NoteOffCommand>()
			.Count(), Is.EqualTo(1),
			"The terminated child cannot emit its later note-off, but the sibling still does.");
		Assert.That(notes.Single(n => n.Commands.Any(c =>
			c is NoteOffCommand)).Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(context.ScopedMemory.ActiveScopeCount, Is.Zero);
	}

	[TestCase(NoteDisplacementAction.Cut, false)]
	[TestCase(NoteDisplacementAction.Continue, true)]
	[TestCase(NoteDisplacementAction.Off, false)]
	[TestCase(NoteDisplacementAction.Fade, false)]
	public void NewNoteActionControlsOldFlattenedProducer(
		NoteDisplacementAction action, bool childContinues)
	{
		DataPatternDefinition parent = Pattern(1, 4);
		PatternCell initial = parent.Grid.GetOrCreateCell(0, 0);
		initial.Note = new StartPatternNote((ObjectId)2U);
		initial.Effects.Add(new TrackerNewNoteActionPatternEffect(action));
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)90U);
		DataPatternDefinition child = Pattern(2, 3);
		child.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child));
		timeline.AddRoot(parent.Id);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.SelectMany(n => n.Commands)
			.OfType<ControlFlattenedSourceCommand>().Single().Action,
			Is.EqualTo(action));
		Assert.That(notes.SelectMany(n => n.Commands)
			.OfType<NoteCutCommand>().Any(), Is.EqualTo(childContinues));
		Assert.That(context.ScopedMemory.ActiveScopeCount, Is.Zero);
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Pattern")
		{
			RowCount = rows, ChannelCount = 2,
		};

	// Musical-output assertions deliberately exclude the private control
	// command which initializes a flattened source's live volume controller.
	private static NoteEvent[] Drain(IncrementalRecursiveTimeline timeline)
		=> DrainRaw(timeline)
			.Select(e => e with {
				Commands = e.Commands.Where(c =>
					c is not BeginFlattenedSourceVolumeCommand).ToArray(),
			})
			.Where(e => e.Commands.Count != 0).ToArray();

	private static NoteEvent[] DrainRaw(IncrementalRecursiveTimeline timeline)
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
