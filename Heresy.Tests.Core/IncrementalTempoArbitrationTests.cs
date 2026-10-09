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
public sealed class IncrementalTempoArbitrationTests
{
	[TestCase(true, 33.0)]
	[TestCase(false, 32.0)]
	public void SimultaneousTxxSlidesUseMappedPhysicalChannelOrderAtClamp(
		bool downThenUp, double endingTempo)
	{
		DataPatternDefinition eagerPattern = new((ObjectId)1U, "Txx order")
		{
			RowCount = 1, ChannelCount = 2,
		};
		eagerPattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(downThenUp ? (byte)0x01 : (byte)0x11));
		eagerPattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerTempoPatternEffect(downThenUp ? (byte)0x11 : (byte)0x01));
		SequencingContext eagerContext = new();
		eagerContext.State.Tempo = 32;
		NoteScheduleBuilder eager = new();
		PatternNoteProcessor.GenerateNotes(
			eagerPattern, eagerContext, eager, out TimeSpan expectedDuration);
		NoteEvent[] baseline = eager.Freeze().ToArray();

		SequencingContext root = new();
		root.State.Tempo = 32;
		using IncrementalPatternTimeline timeline = new(root);
		// Reverse invocation creation order: physical channel order wins.
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(downThenUp ? (byte)0x11 : (byte)0x01))),
			1, root.FlattenedChild(physicalChannelOffset: 1));
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(downThenUp ? (byte)0x01 : (byte)0x11))),
			1, root);

		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Length, Is.EqualTo(baseline.Length));
		for (int i = 0; i < events.Length; i++)
		{
			Assert.That(events[i].Commands, Is.EqualTo(baseline[i].Commands));
			Assert.That(events[i].Offset.TimeOffset,
				Is.EqualTo(baseline[i].Offset.TimeOffset));
		}
		Assert.That(root.State.Tempo, Is.EqualTo(endingTempo));
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(expectedDuration.TotalSeconds).Within(1e-6));
	}

	[Test]
	public void SimultaneousTxxSetAndSlideBeginRampFromSetTempo()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 1));
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(0xFA))), 1, root);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.All.EqualTo(TimeSpan.Zero));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoCommand>().Select(x => x.TicksPerDiachron),
			Is.EqualTo(new[] { 250.0 }));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 255.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(255.0));
	}

	[Test]
	public void LaterTxxSlideInterruptsFromCurrentInstantaneousTempo()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteCutCommand())), 2, root);
		List<NoteEvent> events = [];
		bool spawnedMidpoint = false;
		bool spawnedReplacement = false;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			events.Add(emit.Note);
			if (!spawnedMidpoint
				&& emit.Note.Commands.Any(c => c is SetTempoRampCommand))
			{
				spawnedMidpoint = true;
				timeline.Add(new RawSource(
					At(0.5, 0, new NoteOffCommand())), 1,
					root.FlattenedChild(physicalChannelOffset: 2));
			}
			else if (!spawnedReplacement
				&& emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				spawnedReplacement = true;
				// The child starts at tick 3; a new T11 now begins from
				// the instantaneous 130 rather than the previous end 135.
				timeline.Add(new RawSource(
					At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
					root.FlattenedChild(physicalChannelOffset: 4));
			}
		}
		SetTempoRampCommand[] ramps = events
			.SelectMany(x => x.Commands).OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 135.0, 135.0 }));
		Assert.That(events.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(RampSeconds(125, 130, 3, 3)
				+ RampSeconds(130, 135, 6, 3)).Within(1e-6));
	}

	[Test]
	public void GlobalTempoSetInterruptsRunningRampWithoutRestoringOldEndpoint()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(
			At(0.5, 0, new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 2));

		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			events.Add(emit.Note);
			if (emit.Note.Commands.Any(c => c is NoteOffCommand))
				timeline.Add(new RawSource(At(0, ChannelTarget.Global,
					new SetTempoCommand(250))), 1, root.FlattenedChild());
		}
		double initialHalf = RampSeconds(125, 130, 3, 3);
		Assert.That(events.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(initialHalf + 3 * 2.5 / 250).Within(1e-6));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
	}

	[Test]
	public void GlobalSpeedChangeDoesNotInterruptTempoRamp()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().Single().EndingTempo,
			Is.EqualTo(135.0));
		Assert.That(root.State.Speed, Is.EqualTo(3));
	}

	[Test]
	public void DifferentCapturedRowSpeedsComposePiecewiseWithoutLosingEffectMemory()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));

		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? first), Is.True);
		Assert.That(((IncrementalPatternTimelineStep.Emit)first!).Note.Commands.Single(),
			Is.EqualTo(new SetSpeedCommand(3)));

		SequencingContext second = root.FlattenedChild(physicalChannelOffset: 3);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(0x11))), 1, second);
		NoteEvent[] events = Drain(timeline);
		SetTempoRampCommand[] ramps = events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 132.0, 137.0 }).Within(1e-8));
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 3.0, 3.0 }));
		double breakpoint = RampSeconds(125, 132, 3, 3);
		Assert.That(events.First(e => e.Commands.Any(c =>
			c is SetTempoRampCommand ramp && ramp.EndingTempo == 137))
			.Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(breakpoint).Within(1e-6));
		Assert.That(events.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(breakpoint + RampSeconds(132, 137, 3, 3)).Within(1e-6));
		Assert.That(root.State.Tempo, Is.EqualTo(137).Within(1e-8));
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte a), Is.True);
		Assert.That(second.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte b), Is.True);
		Assert.That(root.GetPhysicalChannelState(3)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out _), Is.False);
		Assert.That(a, Is.EqualTo(0x12));
		Assert.That(b, Is.EqualTo(0x11));
	}

	[Test]
	public void FixedWallDeadlineInsideSecondTempoSegmentUsesAnalyticInverse()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(0, 0, new ApplyTrackerTempoCommand(0x12))),
			1, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1, root.FlattenedChild(physicalChannelOffset: 2));
		Assert.That(timeline.TryStep(out _), Is.True);
		timeline.Add(new RawSource(At(0, 0, new ApplyTrackerTempoCommand(0x11))),
			1, root.FlattenedChild(physicalChannelOffset: 3));
		double boundary = RampSeconds(125, 132, 3, 3);
		TimeSpan deadline = TimeSpan.FromSeconds(boundary + 0.012);
		timeline.Add(new RawSource(new NoteEvent(
			new MusicalTime(deadline, 0), ChannelTarget.Physical(0),
			[new NoteOffCommand()])), 1,
			root.FlattenedChild(physicalChannelOffset: 5));
		double? dueTick = null;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e
				&& e.Note.Commands.Any(c => c is NoteOffCommand))
			{
				Assert.That(e.Note.Offset.TimeOffset, Is.EqualTo(deadline));
				dueTick = e.Tick;
			}
		double slope = 5.0 / 3;
		double expectedTick = 3 + 132 / slope
			* (Math.Exp(slope * 0.012 / 2.5) - 1);
		Assert.That(dueTick, Is.EqualTo(expectedTick).Within(1e-6));
	}

	[Test]
	public void MidRampGlobalTempoSetCancelsRemainingUnequalSpanSegments()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		Assert.That(timeline.TryStep(out _), Is.True);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 3));
		timeline.Add(new RawSource(At(0.5, 0, new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 5));
		bool inserted = false;
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit e)
				continue;
			events.Add(e.Note);
			if (!inserted && e.Note.Commands.Any(c => c is NoteOffCommand))
			{
				inserted = true;
				Assert.That(e.Tick, Is.EqualTo(1.5).Within(1e-8));
				Assert.That(root.State.Tempo, Is.EqualTo(128.5).Within(1e-8));
				timeline.Add(new RawSource(At(0, ChannelTarget.Global,
					new SetTempoCommand(250))), 1,
					root.FlattenedChild(physicalChannelOffset: 6));
			}
		}
		Assert.That(inserted, Is.True);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().Count(), Is.EqualTo(1));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoCommand>().Single().TicksPerDiachron,
			Is.EqualTo(250));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
	}


	[Test]
	public void IndependentSEySlidesComposeAtEachOwnersOwnRepeatBoundary()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long first = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteCutCommand())), 2, root);
		long second = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11)),
			At(1, 0, new NoteOffCommand())), 2,
			root.FlattenedChild(physicalChannelOffset: 2));
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(n => n.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 140.0, 155.0, 165.0 }));
		Assert.That(ramps.Select(x => x.TrackerTicks),
			Is.EqualTo(new[] { 6.0, 6.0, 6.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(165.0));
		Assert.That(timeline.HasOutstandingWork(first), Is.False);
		Assert.That(timeline.HasOutstandingWork(second), Is.False);
		Assert.That(notes.Single(x => x.Commands.Any(c => c is NoteOffCommand))
			.Offset.TimeOffset, Is.LessThan(notes.Single(x =>
				x.Commands.Any(c => c is NoteCutCommand)).Offset.TimeOffset));
	}

	[Test]
	public void RepeatedTxxFromDifferentCapturedSpansComposesAtDistinctBoundaries()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1, root.FlattenedChild(physicalChannelOffset: 2));
		Assert.That(timeline.TryStep(out _), Is.True);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 3));

		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 3.0, 3.0, 3.0, 3.0 }));
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 132.0, 139.0, 146.0, 151.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(151.0));
	}

	[Test]
	public void IndependentSEySetAtRepeatedBoundaryResetsCompetingSlide()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0xFA))), 1, root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.SelectMany(x => x.Commands)
			.OfType<SetTempoCommand>().Select(x => x.TicksPerDiachron),
			Is.EqualTo(new[] { 250.0, 250.0 }));
		Assert.That(notes.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 255.0, 255.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(255.0));
	}

	[Test]
	public void CancellingOneSEyInvocationPreservesOthersFutureTempoSegments()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long cancelled = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		long surviving = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		timeline.Add(new RawSource(At(0.5, 0, new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 4));

		bool cancelledNow = false;
		List<IncrementalPatternTimelineStep.Emit> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			notes.Add(emit);
			if (!cancelledNow && emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				Assert.That(emit.Tick, Is.EqualTo(3.0).Within(1e-8));
				Assert.That(root.State.Tempo, Is.EqualTo(132.5).Within(1e-8));
				Assert.That(timeline.Cancel(cancelled), Is.True);
				cancelledNow = true;
			}
		}
		Assert.That(cancelledNow, Is.True);
		Assert.That(root.State.Tempo, Is.EqualTo(145.0).Within(1e-8));
		Assert.That(notes.Where(e => e.Tick > 3.0 + 1e-8
			&& e.Note.Commands.Any(c => c is SetTempoRampCommand))
			.All(e => e.InvocationId == surviving), Is.True);
	}

	[Test]
	public void WallDeadlineInSecondCrossInvocationRepeatUsesCorrectTickInverse()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetSpeedCommand(3))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		Assert.That(timeline.TryStep(out _), Is.True);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 3));
		double startOfSecond = RampSeconds(125, 132, 3, 3);
		TimeSpan deadline = TimeSpan.FromSeconds(startOfSecond + 0.012);
		timeline.Add(new RawSource(new NoteEvent(
			new MusicalTime(deadline, 0), ChannelTarget.Physical(0),
			[new NoteOffCommand()])), 1,
			root.FlattenedChild(physicalChannelOffset: 5));
		double? due = null;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e
				&& e.Note.Commands.Any(c => c is NoteOffCommand))
			{
				Assert.That(e.Note.Offset.TimeOffset, Is.EqualTo(deadline));
				due = e.Tick;
			}
		double slope = 7.0 / 3.0;
		double expected = 3 + 132 / slope *
			(Math.Exp(slope * 0.012 / 2.5) - 1);
		Assert.That(due, Is.EqualTo(expected).Within(1e-6));
		Assert.That(root.State.Tempo, Is.EqualTo(151));
	}

	[Test]
	public void DirectTempoSetInterruptsAllIndependentSEySourcesWithoutResurrection()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		timeline.Add(new RawSource(At(0.5, 0, new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 4));
		bool interrupted = false;
		List<NoteEvent> output = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			output.Add(emit.Note);
			if (!interrupted && emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				interrupted = true;
				timeline.Add(new RawSource(At(0, ChannelTarget.Global,
					new SetTempoCommand(250))), 1,
					root.FlattenedChild(physicalChannelOffset: 5));
			}
		}
		Assert.That(interrupted, Is.True);
		Assert.That(output.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().Count(), Is.EqualTo(1));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void IncomingTxxExactlyAtSEyRepeatBoundaryComposesOneRamp(
		bool reverseCreation)
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		RawSource delayed = new(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12)));
		RawSource incoming = new(At(1, 0,
			new ApplyTrackerTempoCommand(0x11)));
		SequencingContext incomingContext =
			root.FlattenedChild(physicalChannelOffset: 3);
		if (reverseCreation)
		{
			timeline.Add(incoming, 2, incomingContext);
			timeline.Add(delayed, 1, root);
		}
		else
		{
			timeline.Add(delayed, 1, root);
			timeline.Add(incoming, 2, incomingContext);
		}
		NoteEvent[] events = Drain(timeline);
		SetTempoRampCommand[] ramps = events
			.SelectMany(n => n.Commands).OfType<SetTempoRampCommand>()
			.ToArray();
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 135.0, 150.0 }));
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 6.0, 6.0 }));
		Assert.That(events.Where(e => e.Commands.Any(c =>
			c is SetTempoRampCommand)).Select(e => e.Offset.TimeOffset)
			.Distinct().Count(), Is.EqualTo(2));
		Assert.That(root.State.Tempo, Is.EqualTo(150.0));
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte first),
			Is.True);
		Assert.That(first, Is.EqualTo(0x12));
		Assert.That(incomingContext.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte second),
			Is.True);
		Assert.That(root.GetPhysicalChannelState(3)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out _), Is.False);
		Assert.That(second, Is.EqualTo(0x11));
	}

	[Test]
	public void IncomingTxxSetAndSEyImmediateResetAtSameTickHonorMappedChannels()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		// The repeated TFA owns mapped channel 4; at tick 6 the
		// newly arriving T80 on channel 0 must execute *before* its
		// repeated reset, regardless of creation order.
		timeline.Add(new RawSource(At(1, 0,
			new ApplyTrackerTempoCommand(0x80))), 2, root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0xFA))), 1,
			root.FlattenedChild(physicalChannelOffset: 4));

		var events = Drain(timeline);
		var sets = events.Where(e => e.Commands.Any(c =>
			c is SetTempoCommand)).ToArray();
		Assert.That(sets.SelectMany(e => e.Commands)
			.OfType<SetTempoCommand>().Select(c => c.TicksPerDiachron),
			Is.EqualTo(new[] { 250.0, 128.0, 250.0 }));
		Assert.That(sets.Select(e => e.Target.PhysicalChannel),
			Is.EqualTo(new[] { 4, 0, 4 }));
		Assert.That(root.State.Tempo, Is.EqualTo(250.0));
	}

	[Test]
	public void GlobalTempoAtSEyBoundarySuppressesOldRepeatedRamp()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(At(1, ChannelTarget.Global,
			new SetTempoCommand(200))), 2,
			root.FlattenedChild(physicalChannelOffset: 3));
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 135.0 }));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoCommand>().Select(x => x.TicksPerDiachron),
			Is.EqualTo(new[] { 200.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(200.0));
	}

	[Test]
	public void NewRepeatingTxxAtOldRepeatBoundaryRetainsIndependentOwnership()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long first = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(3)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, root);
		long second = timeline.Add(new RawSource(
			At(1, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(1, 0, new ApplyTrackerTempoCommand(0x11))), 2,
			root.FlattenedChild(physicalChannelOffset: 3));
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 135.0, 150.0, 165.0, 175.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(175.0));
		Assert.That(timeline.HasOutstandingWork(first), Is.False);
		Assert.That(timeline.HasOutstandingWork(second), Is.False);
	}

	private static double RampSeconds(
		double start, double end, double ticks, double into)
		=> 2.5 * ticks / (end - start)
			* Math.Log((start + (end - start) * into / ticks) / start);

	private static NoteEvent At(double row, int channel, NoteCommand command)
		=> At(row, ChannelTarget.Physical(channel), command);

	private static NoteEvent At(double row, ChannelTarget target, NoteCommand command)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, [command]);

	private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				notes.Add(emit.Note);
		return notes.ToArray();
	}

	private sealed class RawSource(params NoteEvent[] events) : IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent e in events)
				yield return new RawPatternStep.Emit(e);
		}
	}
}
