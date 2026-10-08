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
public sealed class IncrementalPatternTimelineTests
{
	[Test]
	public void FractionalParentAndChildEventsInterleaveOnSharedTickClock()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0.5, 0, new NoteCutCommand()),
			At(1.0, 0, new NoteOffCommand())), 2, root);
		timeline.Add(new RawSource(
			At(0.25, 0, new NoteOffCommand()),
			At(1.5, 0, new NoteCutCommand())), 2,
			root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] notes = DrainNotes(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(30),
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(180),
			}));
		Assert.That(notes.Select(n => n.Target),
			Is.EqualTo(new[]
			{
				ChannelTarget.Physical(2),
				ChannelTarget.Physical(0),
				ChannelTarget.Physical(0),
				ChannelTarget.Physical(2),
			}));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void ExplicitFlattenedChildUsesMappedChannelsAndSharedClock()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0.5, 0, new NoteCutCommand())), 2, root);
		long child = timeline.AddFlattenedChild(new RawSource(
			At(0.25, 1, new NoteOffCommand())), 2, root,
			physicalChannelOffset: 4);

		Assert.That(timeline.HasUnfinishedRows(child), Is.True);
		NoteEvent[] notes = DrainNotes(timeline);
		Assert.That(notes.Select(n => n.Target),
			Is.EqualTo(new[] {
				ChannelTarget.Physical(5),
				ChannelTarget.Physical(0),
			}));
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.FromMilliseconds(30),
				TimeSpan.FromMilliseconds(60),
			}));
		Assert.That(timeline.HasOutstandingWork(child), Is.False);
	}

	[Test]
	public void ExplicitFlattenedChildRejectsParentFromDifferentTimeline()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		Assert.Throws<ArgumentException>(() =>
			timeline.AddFlattenedChild(
				new RawSource(At(0, 0, new NoteCutCommand())),
				1, new SequencingContext()));
	}

	[Test]
	public void ChildTempoSetAtFractionalTickMovesParentAndChildLaterEvents()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0.5, 0, new NoteCutCommand()),
			At(1.5, 0, new NoteOffCommand())), 2, root);
		timeline.Add(new RawSource(
			At(0.25, ChannelTarget.Global, new SetTempoCommand(250)),
			At(1.5, 1, new NoteCutCommand())), 2,
			root.FlattenedChild(physicalChannelOffset: 3));

		NoteEvent[] notes = DrainNotes(timeline);
		// Tempo(0.25) follows established tracker timing semantics:
		// timing commands execute at the beginning of the specified row.
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(30),
				TimeSpan.FromMilliseconds(90),
				TimeSpan.FromMilliseconds(90),
			}));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void SpeedChangeAtLaterChildRowDoesNotResizeActiveParentRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(1.0, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(
			At(0.0, ChannelTarget.Global, new SetSpeedCommand(3)),
			At(1.0, 1, new NoteOffCommand())), 2,
			root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] notes = DrainNotes(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(120),
			}));
		Assert.That(notes.Last().Target, Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void FutureSourceAndTempoStayUnmodifiedUntilTheirActualEvents()
	{
		DataPatternDefinition parent = new((ObjectId)1U, "Tracker")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		PatternCell future = parent.Grid.GetOrCreateCell(2, 0);
		future.SourceId = (ObjectId)42U;
		future.Effects.Add(new SetTempoPatternEffect(250));
		future.Note = new StartPatternNote();
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(parent, parent.RowCount, root);

		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? first), Is.True);
		Assert.That(first, Is.TypeOf<IncrementalPatternTimelineStep.Advance>());
		Assert.That(root.State.Tempo, Is.EqualTo(125));
		Assert.That(root.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo(ObjectId.None));
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			// The global Tempo event precedes the physical-channel event
			// on the same row. Source selection executes with the latter.
			if (step is IncrementalPatternTimelineStep.Emit emit
				&& emit.Note.Commands.Any(c => c is StartNoteCommand))
				break;
		}
		Assert.That(root.State.Tempo, Is.EqualTo(250));
		Assert.That(root.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo((ObjectId)42U));
	}

	[Test]
	public void SameTimeCommandsHaveDeterministicCursorAndChannelOrder()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(0, 1, new NoteCutCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 2));
		timeline.Add(new RawSource(At(0, 0, new NoteOffCommand())), 1, root);
		timeline.Add(new RawSource(At(0, 0, new NoteCutCommand())), 1, root);

		NoteEvent[] notes = DrainNotes(timeline);
		Assert.That(notes.Select(n => n.Commands.Single().GetType()),
			Is.EqualTo(new[] {
				typeof(NoteOffCommand),
				typeof(NoteCutCommand),
				typeof(NoteCutCommand),
			}));
		Assert.That(notes.Select(n => n.Target.PhysicalChannel),
			Is.EqualTo(new[] { 0, 0, 3 }));
	}

	[Test]
	public void SilentLargePatternReturnsControlBeforeItsEnd()
	{
		DataPatternDefinition silent = new((ObjectId)1U, "Silence")
		{
			RowCount = 10000,
			ChannelCount = 1,
		};
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(silent, silent.RowCount, context);

		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? step), Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Advance>());
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void ChildStartedAtFractionalNoteCanChangeTempoWithinParentRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0.5, 0, new StartNoteCommand((ObjectId)42U)),
			At(1.0, 0, new NoteCutCommand())), 2, root);

		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			notes.Add(emit.Note);
			if (emit.Note.Commands.Any(c => c is StartNoteCommand))
			{
				timeline.Add(new RawSource(
					At(0, ChannelTarget.Global, new SetTempoCommand(250))),
					1, root.FlattenedChild(physicalChannelOffset: 0));
			}
		}

		Assert.That(notes.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(90),
			}));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(150)));
	}

	[Test]
	public void CancelledInvocationDisposesItsCursorAndDoesNotEmitLaterNotes()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long id = timeline.Add(new RawSource(
			At(0.5, 0, new NoteCutCommand()),
			At(1.5, 0, new NoteOffCommand())), 2, root);
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Emit)
				break;
		}
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(timeline.Cancel(id), Is.False);
		Assert.That(timeline.IsComplete, Is.True);
		Assert.That(timeline.TryStep(out _), Is.False);
	}

	[Test]
	public void DecreasingRawRowPositionsAreRejectedBeforeResolvingLaterCommand()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(1.0, 0, new NoteOffCommand()),
			At(0.5, 0, new NoteCutCommand())), 2, root);

		Assert.Throws<InvalidOperationException>(() => DrainNotes(timeline));
	}

	[Test]
	public void SkippedRowsDoNotExecuteGlobalSpeedOrChannelCommands()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, ChannelTarget.Global, new SetSpeedCommand(3)),
			At(1, 0, new NoteOffCommand()),
			At(2, 0, new NoteCutCommand())), 3, root, startRow: 2);

		NoteEvent[] notes = DrainNotes(timeline);
		Assert.That(notes, Has.Length.EqualTo(1));
		Assert.That(notes[0].Commands.Single(), Is.TypeOf<NoteCutCommand>());
		Assert.That(notes[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(root.State.Speed, Is.EqualTo(6));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void UnsupportedAdvancedCommandFailsInsteadOfSilentlyChangingSemantics()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyArpeggioCommand(0x12))), 1, root);
		Assert.Throws<NotSupportedException>(
			() => timeline.TryStep(out _));
	}

	[Test]
	public void UnboundedSameRowEmissionIsStoppedByCooperationBudget()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RepeatingSameRowSource(), 1, root);
		Assert.Throws<InvalidOperationException>(
			() => timeline.TryStep(out _));
	}

	[Test]
	public void CpuCooperationSuspendsPartiallyBufferedRowBeforeCommittingTempo()
	{
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long id = timeline.Add(new CooperativeRawSource(
			new RawPatternStep.Emit(At(0, ChannelTarget.Global,
				new SetTempoCommand(250))),
			new RawPatternStep.Cooperate(0),
			new RawPatternStep.Emit(At(0.5, 0, new NoteCutCommand())),
			new RawPatternStep.Advance(1)), 1, context);

		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? step),
			Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Cooperate>());
		var checkpoint = (IncrementalPatternTimelineStep.Cooperate)step!;
		Assert.That(checkpoint.InvocationId, Is.EqualTo(id));
		Assert.That(checkpoint.Tick, Is.Zero);
		Assert.That(checkpoint.Time, Is.EqualTo(TimeSpan.Zero));
		Assert.That(timeline.Tick, Is.Zero);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.Zero));
		Assert.That(context.State.Tempo, Is.EqualTo(125));

		Assert.That(timeline.TryStep(out step), Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Emit>());
		Assert.That(((IncrementalPatternTimelineStep.Emit)step!).Note
			.Commands.Single(), Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(context.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.Zero));

		Assert.That(timeline.TryStep(out step), Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Emit>());
		Assert.That(((IncrementalPatternTimelineStep.Emit)step!).Note
			.Commands.Single(), Is.TypeOf<NoteCutCommand>());
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(30)));
		DrainNotes(timeline);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(60)));
	}

	[Test]
	public void RawTimelineSilentlyDropsEarlierEventsButKeepsEqualRowOrder()
	{
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(new RawSource(
			At(4, 0, new NoteCutCommand()),
			At(2, 0, new NoteOffCommand()),
			At(3, 0, new NoteOffCommand()),
			At(4, 0, new NoteOffCommand()),
			At(5, 0, new NoteCutCommand())), 6, context);

		NoteEvent[] events = DrainNotes(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset), Is.EqualTo(
			new[] { TimeSpan.FromMilliseconds(480),
				TimeSpan.FromMilliseconds(480),
				TimeSpan.FromMilliseconds(600) }));
		Assert.That(events.Select(e => e.Commands.Single().GetType()), Is.EqualTo(
			new[] { typeof(NoteCutCommand), typeof(NoteOffCommand),
				typeof(NoteCutCommand) }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(720)));
	}

	[Test]
	public void CpuCheckpointAtFractionalPositionDoesNotMoveMusicalClock()
	{
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(new CooperativeRawSource(
			new RawPatternStep.Emit(At(0.75, 0, new NoteOffCommand())),
			new RawPatternStep.Cooperate(0.75),
			new RawPatternStep.Emit(At(0.9, 0, new NoteCutCommand()))),
			1, context);

		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? step),
			Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Cooperate>());
		Assert.That(timeline.Tick, Is.Zero);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.Zero));
		NoteEvent[] events = DrainNotes(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(90),
				TimeSpan.FromMilliseconds(108),
			}));
	}

	[Test]
	public void EndlessCpuCooperationReturnsControlAndCancellationDisposesSource()
	{
		SequencingContext context = new();
		InfiniteCooperationRawSource source = new();
		using IncrementalPatternTimeline timeline = new(context);
		long id = timeline.Add(source, 1, context);

		for (int i = 0; i < 3; i++)
		{
			Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? step),
				Is.True);
			Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Cooperate>());
			Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.Zero));
			Assert.That(timeline.HasUnfinishedRows(id), Is.True);
		}

		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(source.Disposed, Is.True);
		Assert.That(timeline.IsComplete, Is.True);
	}

	private sealed class CooperativeRawSource(params RawPatternStep[] steps)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (RawPatternStep step in steps)
				yield return step;
		}
	}

	private sealed class InfiniteCooperationRawSource
		: IIncrementalRawPatternNoteGenerator
	{
		public bool Disposed { get; private set; }

		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			try
			{
				while (true)
					yield return new RawPatternStep.Cooperate(0);
			}
			finally
			{
				Disposed = true;
			}
		}
	}

	private static NoteEvent At(double row, int channel, NoteCommand command)
		=> At(row, ChannelTarget.Physical(channel), command);

	private static NoteEvent At(double row, ChannelTarget target, NoteCommand command)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, [command]);

	private static NoteEvent[] DrainNotes(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Emit emit)
				notes.Add(emit.Note);
		}
		return notes.ToArray();
	}

	private sealed class RawSource(params NoteEvent[] events)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent e in events)
				yield return new RawPatternStep.Emit(e);
		}
	}

	private sealed class RepeatingSameRowSource : IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			while (true)
				yield return new RawPatternStep.Emit(
					At(0, 0, new NoteCutCommand()));
		}
	}
}
