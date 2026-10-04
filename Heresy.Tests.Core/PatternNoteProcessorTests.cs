using System;
using System.Collections.Generic;

using NUnit.Framework;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PatternNoteProcessorTests
{
	[Test]
	public void ResolvesFractionalRowsUsingCurrentRowDuration()
	{
		TestPatternGenerator generator = new(
			2.0,
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand()));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			generator,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.RowOffset, Is.EqualTo(0.0));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void TimingChangeAtRowBoundaryChangesThatRowsDuration()
	{
		TestPatternGenerator generator = new(
			2.0,
			Event(1.0, ChannelTarget.Global, new SetSpeedCommand(3)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand()));
		NoteScheduleBuilder output = new();
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(generator, context, output, out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(150)));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
		Assert.That(context.State.Speed, Is.EqualTo(3));
	}

	[Test]
	public void FractionalPartOfTimingEventRowOffsetIsIgnored()
	{
		TestPatternGenerator generator = new(
			2.0,
			Event(1.75, ChannelTarget.Global, new SetSpeedCommand(3)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand()));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(generator, new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(150)));
	}

	[Test]
	public void FixedOffsetDefersTimingChangeToNextRowBoundary()
	{
		TestPatternGenerator generator = new(
			3.0,
			Event(1.0, TimeSpan.FromMilliseconds(1), ChannelTarget.Global, new SetSpeedCommand(3)),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand()),
			Event(2.5, ChannelTarget.Physical(0), new NoteOffCommand()));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(generator, new SequencingContext(), output, out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(3));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(schedule[2].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(270)));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(300)));
	}

	[Test]
	public void StartRowSkipsEventsWithoutExecutingTheirTimingChanges()
	{
		TestPatternGenerator generator = new(
			6.0,
			Event(2.0, ChannelTarget.Global, new SetSpeedCommand(3)),
			Event(5.0, ChannelTarget.Physical(0), new NoteCutCommand()));
		NoteScheduleBuilder output = new();
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(generator, context, output, 4, out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(context.State.Speed, Is.EqualTo(SequencingConstants.DefaultSpeed));
	}

	[Test]
	public void StartRowPreservesTimeDisplacedEventThatCrossesTheNewOrigin()
	{
		TestPatternGenerator generator = new(
			20.0,
			Event(2.0, TimeSpan.FromMilliseconds(400), ChannelTarget.Physical(0), new NoteCutCommand()));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(generator, new SequencingContext(), output, 4, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(160)));
	}

	[Test]
	public void EqualTimeEventsAreOrderedByPhysicalChannelThenEmissionOrder()
	{
		TestPatternGenerator generator = new(
			1.0,
			Event(0.0, ChannelTarget.Physical(7), new NoteCutCommand()),
			Event(0.0, ChannelTarget.Physical(2), new NoteCutCommand()),
			Event(0.0, ChannelTarget.Physical(2), new NoteOffCommand()));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(generator, new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule[0].Target.PhysicalChannel, Is.EqualTo(2));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<NoteCutCommand>());
		Assert.That(schedule[1].Target.PhysicalChannel, Is.EqualTo(2));
		Assert.That(schedule[1].Commands[0], Is.TypeOf<NoteOffCommand>());
		Assert.That(schedule[2].Target.PhysicalChannel, Is.EqualTo(7));
	}

	[Test]
	public void PhysicalTargetsAreOffsetByContextChannelBase()
	{
		TestPatternGenerator generator = new(
			1.0,
			Event(0.0, ChannelTarget.Physical(2), new NoteCutCommand()));
		NoteScheduleBuilder output = new();
		SequencingContext context = new(physicalChannelBase: 5);

		PatternNoteProcessor.GenerateNotes(generator, context, output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule[0].Target, Is.EqualTo(ChannelTarget.Physical(7)));
	}

	[Test]
	public void FlattenedChildrenAccumulateChannelBaseWhileMixdownStartsLocalChannelSpace()
	{
		SequencingContext root = new(physicalChannelBase: 3);
		SequencingContext child = root.FlattenedChild(physicalChannelOffset: 4);
		SequencingContext grandchild = child.FlattenedChild(physicalChannelOffset: 5);
		SequencingContext mixdown = grandchild.MixdownChild();

		Assert.That(child.PhysicalChannelBase, Is.EqualTo(7));
		Assert.That(child.MapPhysicalChannel(2), Is.EqualTo(9));
		Assert.That(grandchild.PhysicalChannelBase, Is.EqualTo(12));
		Assert.That(grandchild.MapPhysicalChannel(1), Is.EqualTo(13));
		Assert.That(mixdown.PhysicalChannelBase, Is.EqualTo(0));
		Assert.That(mixdown.MapPhysicalChannel(3), Is.EqualTo(3));
		Assert.That(child.State, Is.SameAs(root.State));
		Assert.That(mixdown.State, Is.Not.SameAs(grandchild.State));
		Assert.That(mixdown.State.Tempo, Is.EqualTo(grandchild.State.Tempo));
		Assert.That(mixdown.State.Speed, Is.EqualTo(grandchild.State.Speed));
	}

	private static NoteEvent Event(double rowOffset, ChannelTarget target, params NoteCommand[] commands)
		=> Event(rowOffset, TimeSpan.Zero, target, commands);

	private static NoteEvent Event(
		double rowOffset,
		TimeSpan timeOffset,
		ChannelTarget target,
		params NoteCommand[] commands)
		=> new(new MusicalTime(timeOffset, rowOffset), target, commands);

	private sealed class TestPatternGenerator : IRawPatternNoteGenerator
	{
		private readonly IReadOnlyList<NoteEvent> _events;
		private readonly double _rowCount;

		public TestPatternGenerator(double rowCount, params NoteEvent[] events)
		{
			_rowCount = rowCount;
			_events = events;
		}

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			foreach (NoteEvent noteEvent in _events)
				output.Append(noteEvent);

			rowCount = _rowCount;
		}
	}
}
