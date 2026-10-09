using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalPatternDeferredTimingTests
{
	[Test]
	public void DeferredTempoWaitsForNextEligibleOwnRowBoundaryAndMatchesEagerProcessor()
	{
		RawSource source = new(
			Event(0, TimeSpan.FromMilliseconds(1), ChannelTarget.Global,
				new SetTempoCommand(250)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand()),
			Event(2, ChannelTarget.Physical(0), new NoteOffCommand()));
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(source, 3, context);
		NoteEvent[] actual = Drain(timeline);

		NoteScheduleBuilder expectedBuilder = new();
		PatternNoteProcessor.GenerateNotes(
			new EagerSource(source.Events, 3), new SequencingContext(),
			expectedBuilder, out TimeSpan duration);
		NoteEvent[] expected = expectedBuilder.Freeze().ToArray();

		Assert.That(actual.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < actual.Length; i++)
		{
			Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands));
			Assert.That(actual[i].Offset.TimeOffset,
				Is.EqualTo(expected[i].Offset.TimeOffset));
		}
		Assert.That(actual.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(150),
				TimeSpan.FromMilliseconds(180),
			}));
		Assert.That(timeline.Elapsed, Is.EqualTo(duration));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(context.State.Tempo, Is.EqualTo(250));
	}

	[Test]
	public void DeferredSpeedAppliesAtNextBoundaryWithoutResizingAnotherStartedRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(10), ChannelTarget.Global,
				new SetSpeedCommand(3)),
			Event(1.5, ChannelTarget.Physical(0), new NoteOffCommand())),
			2, root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(150),
				TimeSpan.FromMilliseconds(180),
			}));
		Assert.That(events[0].Commands.Single(), Is.EqualTo(new SetSpeedCommand(3)));
		Assert.That(events[1].Target, Is.EqualTo(ChannelTarget.Physical(2)));
		Assert.That(events[2].Target, Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(root.State.Speed, Is.EqualTo(3));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void MultipleDeferredTempoCommandsExecuteInEligibilityOrderOnSameBoundary()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(40), ChannelTarget.Global,
				new SetTempoCommand(200)),
			Event(0, TimeSpan.FromMilliseconds(20), ChannelTarget.Global,
				new SetTempoCommand(250)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand())),
			2, root);

		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(157.5),
			}));
		Assert.That(events[0].Commands.Single(), Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(events[1].Commands.Single(), Is.EqualTo(new SetTempoCommand(200)));
		Assert.That(root.State.Tempo, Is.EqualTo(200));
	}

	[Test]
	public void DeferredTempoWaitsBeyondFirstBoundaryIfDeadlineIsLater()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(170), ChannelTarget.Global,
				new SetTempoCommand(250)),
			Event(1, ChannelTarget.Physical(0), new NoteCutCommand()),
			Event(2, ChannelTarget.Physical(0), new NoteOffCommand())),
			3, root);

		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240),
				TimeSpan.FromMilliseconds(240),
			}));
		Assert.That(notes[1].Commands.Single(), Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(300)));
	}

	[Test]
	public void DeferredTimingCancelledBeforeNextRowCannotChangeGlobalState()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long invocation = timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(1), ChannelTarget.Global,
				new SetTempoCommand(250))), 3, root);
		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? first), Is.True);
		Assert.That(first, Is.TypeOf<IncrementalPatternTimelineStep.Advance>());
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(root.State.Tempo, Is.EqualTo(125));
		Assert.That(timeline.Cancel(invocation), Is.True);
		Assert.That(timeline.TryStep(out _), Is.False);
		Assert.That(root.State.Tempo, Is.EqualTo(125));
	}

	[Test]
	public void DeferredTimingWithoutFutureEligibleRowIsDiscarded()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(1), ChannelTarget.Global,
				new SetSpeedCommand(3))), 1, root);

		Assert.That(Drain(timeline), Is.Empty);
		Assert.That(root.State.Speed, Is.EqualTo(6));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FractionalDeferredTimingUsesTheBeginningOfItsNominalRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0.75, TimeSpan.FromMilliseconds(1), ChannelTarget.Global,
				new SetTempoCommand(250)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand())),
			2, root);

		NoteEvent[] events = Drain(timeline);
		Assert.That(events[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(events[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(150)));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
	}

	[Test]
	public void EligibilityIsCheckedAtOwnersOwnRowStartAfterAnotherCursorChangesTempo()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, TimeSpan.FromMilliseconds(30), ChannelTarget.Global,
				new SetTempoCommand(200)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand())),
			2, root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Global, new SetTempoCommand(250))),
			1, root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(97.5),
			}));
		Assert.That(notes[0].Commands.Single(), Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(notes[1].Commands.Single(), Is.EqualTo(new SetTempoCommand(200)));
		Assert.That(root.State.Tempo, Is.EqualTo(200));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(135)));
	}

	[Test]
	public void GlobalTimingAtFinalPatternEndpointDoesNotApplyToLastRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(2, ChannelTarget.Global, new SetSpeedCommand(3))), 2, root);

		Assert.That(Drain(timeline), Is.Empty);
		Assert.That(root.State.Speed, Is.EqualTo(6));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void NegativeAndMixedFixedOffsetTimingRemainExplicitlyUnsupported()
	{
		foreach (NoteEvent raw in new[]
		{
			Event(0, TimeSpan.FromMilliseconds(-1), ChannelTarget.Global,
				new SetTempoCommand(250)),
			new NoteEvent(
				new MusicalTime(TimeSpan.FromMilliseconds(20), 0),
				ChannelTarget.Global,
				new NoteCommand[] { new SetTempoCommand(250), new NoteCutCommand() }),
		})
		{
			SequencingContext root = new();
			using IncrementalPatternTimeline timeline = new(root);
			timeline.Add(new RawSource(raw), 2, root);
			Assert.Throws<NotSupportedException>(() => timeline.TryStep(out _));
		}
	}

	[TestCase("SCx")]
	[TestCase("SDx")]
	[TestCase("Qxy")]
	[TestCase("SDx+Qxy")]
	public void TrackerTickEffectsWithPositiveFixedWallOffsetMatchEager(
		string kind)
	{
		NoteCommand[] commands = kind switch
		{
			"SCx" => [new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
				new ApplyTrackerNoteCutCommand(3)],
			"SDx" => [new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
				new ApplyTrackerNoteDelayCommand(2)],
			"Qxy" => [new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
				new ApplyRetriggerCommand(0x03)],
			_ => [new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
				new ApplyTrackerNoteDelayCommand(2),
				new ApplyRetriggerCommand(0x03)],
		};
		RawSource source = new(Event(0,
			TimeSpan.FromMilliseconds(15), ChannelTarget.Physical(0),
			commands));
		AssertTrackerWallParity(source, 1);
	}

	[Test]
	public void SDxQxyWallOffsetsFollowSEyAndS6xRepeatTicks()
	{
		RawSource source = new(Event(0,
			TimeSpan.FromMilliseconds(8), ChannelTarget.Physical(0),
			new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
			new ApplyTrackerFinePatternDelayCommand(2),
			new ApplyTrackerPatternDelayCommand(1),
			new ApplyTrackerNoteDelayCommand(3),
			new ApplyRetriggerCommand(0x03)));
		AssertTrackerWallParity(source, 1);
	}

	[Test]
	public void TrackerWallOffsetsBeyondOriginalRowEndDoNotEmitLateNotes()
	{
		RawSource source = new(Event(0,
			TimeSpan.FromMilliseconds(110), ChannelTarget.Physical(0),
			new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
			new ApplyTrackerNoteDelayCommand(2),
			new ApplyRetriggerCommand(0x03)));
		AssertTrackerWallParity(source, 1);
	}

	[Test]
	public void ConcurrentTempoChangeRetimesTickButPreservesFixedWallOffset()
	{
		RawSource source = new(
			Event(0, TimeSpan.Zero, ChannelTarget.Global,
				new SetTempoCommand(250)),
			Event(0, TimeSpan.FromMilliseconds(15), ChannelTarget.Physical(0),
				new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
				new ApplyTrackerNoteDelayCommand(2),
				new ApplyRetriggerCommand(0x03),
				new ApplyTrackerNoteCutCommand(4)));
		AssertTrackerWallParity(source, 1);
	}

	[Test]
	public void CancelDropsSyntheticTrackerWallDeadline()
	{
		RawSource delayed = new(Event(0,
			TimeSpan.FromMilliseconds(30), ChannelTarget.Physical(0),
			new StartNoteCommand((Heresy.Core.Objects.ObjectId)10U),
			new ApplyTrackerNoteDelayCommand(2)));
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long owner = timeline.Add(delayed, 1, root);
		timeline.Add(new RawSource(Event(0.5, TimeSpan.Zero,
			ChannelTarget.Physical(0), new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		bool canceled = false;
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			events.Add(emit.Note);
			if (emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				canceled = timeline.Cancel(owner);
				break;
			}
		}
		Assert.That(canceled, Is.True);
		events.AddRange(Drain(timeline));
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>(), Is.Empty);
	}

	private static void AssertTrackerWallParity(RawSource source, int rowCount)
	{
		SequencingContext eager = new();
		NoteScheduleBuilder expected = new();
		PatternNoteProcessor.GenerateNotes(new EagerSource(source.Events, rowCount),
			eager, expected, out TimeSpan duration);
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(source, rowCount, context);
		NoteEvent[] actual = Drain(timeline);
		NoteEvent[] baseline = expected.Freeze().ToArray();
		Assert.That(actual.Length, Is.EqualTo(baseline.Length));
		for (int i = 0; i < baseline.Length; i++)
		{
			Assert.That(actual[i].Target, Is.EqualTo(baseline[i].Target));
			Assert.That(actual[i].Commands, Is.EqualTo(baseline[i].Commands));
			Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(baseline[i].Offset.TimeOffset.TotalSeconds)
					.Within(1e-6));
		}
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(duration.TotalSeconds).Within(1e-6));
		Assert.That(context.GetPhysicalChannelState(0).RetriggerCountdown,
			Is.EqualTo(eager.GetPhysicalChannelState(0).RetriggerCountdown));
	}

	private static NoteEvent Event(
		double row, ChannelTarget target, params NoteCommand[] commands)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, commands);

	private static NoteEvent Event(
		double row, TimeSpan offset, ChannelTarget target, params NoteCommand[] commands)
		=> new(new MusicalTime(offset, row), target, commands);

	private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				notes.Add(emit.Note);
		return notes.ToArray();
	}

	private sealed class RawSource(params NoteEvent[] notes) : IIncrementalRawPatternNoteGenerator
	{
		public IReadOnlyList<NoteEvent> Events { get; } = notes;
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in notes)
				yield return new RawPatternStep.Emit(note);
		}
	}

	private sealed class EagerSource(
		IReadOnlyList<NoteEvent> notes, double rowCount) : IRawPatternNoteGenerator
	{
		public void GenerateRawNotes(
			SequencingContext context, INoteReceiver output, out double totalRows)
		{
			foreach (NoteEvent note in notes)
				output.Append(note);
			totalRows = rowCount;
		}
	}
}
