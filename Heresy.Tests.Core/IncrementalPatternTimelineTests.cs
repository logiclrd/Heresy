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
			if (step is IncrementalPatternTimelineStep.Emit)
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
	public void DecreasingRawRowPositionsAreRejectedBeforeResolvingLaterCommand()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			At(1.0, 0, new NoteOffCommand()),
			At(0.5, 0, new NoteCutCommand())), 2, root);

		Assert.Throws<InvalidOperationException>(() => DrainNotes(timeline));
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
}
