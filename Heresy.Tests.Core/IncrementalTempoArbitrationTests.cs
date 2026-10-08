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
