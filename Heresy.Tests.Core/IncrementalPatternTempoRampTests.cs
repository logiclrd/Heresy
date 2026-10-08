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
public sealed class IncrementalPatternTempoRampTests
{
	[TestCase((byte)0x12, 125.0, 135.0)]
	[TestCase((byte)0x02, 35.0, 32.0)]
	[TestCase((byte)0x1F, 250.0, 255.0)]
	public void TxxSlideAndFractionalNoteMatchEagerProcessor(
		byte parameter, double startingTempo, double endingTempo)
	{
		DataPatternDefinition pattern = Pattern(1, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(parameter));
		// The independent note needs no instrument to emit a Cut.
		pattern.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		// The grid note is at row start; the additional fractional note
		// ensures another cursor observes the tempo at half a row.
		RawSource source = new(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(parameter)),
			Event(0.5, ChannelTarget.Physical(1),
				new NoteCutCommand()));

		SequencingContext eagerContext = new();
		eagerContext.State.Tempo = startingTempo;
		NoteScheduleBuilder eager = new();
		PatternNoteProcessor.GenerateNotes(
			new EagerSource(source.Events, 1), eagerContext, eager,
			out TimeSpan eagerDuration);

		SequencingContext context = new();
		context.State.Tempo = startingTempo;
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(source, 1, context);
		NoteEvent[] actual = Drain(timeline);
		NoteEvent[] expected = eager.Freeze().ToArray();
		Assert.That(actual.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < expected.Length; i++)
		{
			Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target));
			Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands));
			Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds).Within(1e-6));
		}
		Assert.That(context.State.Tempo, Is.EqualTo(endingTempo).Within(1e-8));
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(eagerDuration.TotalSeconds).Within(1e-6));
	}

	[Test]
	public void ChildTempoRampRetimesParentFractionalEventsWithinItsActiveRow()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0.5, ChannelTarget.Physical(0), new NoteCutCommand()),
			Event(1, ChannelTarget.Physical(0), new NoteOffCommand())), 2, root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x12))), 1,
			root.FlattenedChild(physicalChannelOffset: 3));

		NoteEvent[] events = Drain(timeline);
		double middle = RampSeconds(125, 135, 6, 3);
		double ending = RampSeconds(125, 135, 6, 6);
		Assert.That(events.Select(e => e.Offset.TimeOffset.TotalSeconds),
			Is.EqualTo(new[] { 0.0, middle, ending }).Within(1e-6));
		Assert.That(events[0].Target, Is.EqualTo(ChannelTarget.Physical(3)));
		Assert.That(root.State.Tempo, Is.EqualTo(135.0));
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(ending + 2.5 * 6 / 135).Within(1e-6));
	}

	[Test]
	public void FixedWallDeadlineInsideRampUsesAnalyticTickInversion()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(
			new NoteEvent(
				new MusicalTime(TimeSpan.FromMilliseconds(65), 0),
				ChannelTarget.Physical(0), [new NoteOffCommand()])),
			1, root.FlattenedChild(physicalChannelOffset: 2));

		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes, Has.Length.EqualTo(2));
		Assert.That(notes[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(65)));
		double slope = 10.0 / 6.0;
		double expectedTick =
			125.0 / slope * (Math.Exp(slope * 0.065 / 2.5) - 1.0);
		// Advance steps expose the shared tracker-tick position at
		// each cooperative observation. The delayed note must be in the
		// interval and must not have been scheduled by constant Tempo.
		using IncrementalPatternTimeline second = new(new SequencingContext());
		SequencingContext context = new();
		using IncrementalPatternTimeline measured = new(context);
		measured.Add(new RawSource(Event(0, ChannelTarget.Physical(0),
			new ApplyTrackerTempoCommand(0x12))), 1, context);
		measured.Add(new RawSource(new NoteEvent(
			new MusicalTime(TimeSpan.FromMilliseconds(65), 0),
			ChannelTarget.Physical(0), [new NoteOffCommand()])),
			1, context.FlattenedChild(physicalChannelOffset: 2));
		while (measured.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Emit emit
				&& emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				Assert.That(emit.Tick, Is.EqualTo(expectedTick).Within(1e-6));
				break;
			}
		}
	}

	[Test]
	public void T00RemembersPriorSlideAcrossRows()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x12)),
			Event(1, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x00))), 2, root);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>()
			.Select(c => c.EndingTempo), Is.EqualTo(new[] { 135.0, 145.0 }));
		Assert.That(root.State.Tempo, Is.EqualTo(145.0));
		Assert.That(root.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte memory), Is.True);
		Assert.That(memory, Is.EqualTo(0x12));
	}

	[Test]
	public void ImmediateTfaAppliesAtRowStartAndT00RecallsTheSet()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0xFA)),
			Event(1, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x00))), 2, root);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.SelectMany(e => e.Commands)
			.OfType<SetTempoCommand>().Select(c => c.TicksPerDiachron),
			Is.EqualTo(new[] { 250.0, 250.0 }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void DeferredTxxSlideStartsAtEligibleLaterRowBoundary()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			new NoteEvent(
				new MusicalTime(TimeSpan.FromMilliseconds(1), 0),
				ChannelTarget.Physical(0), [new ApplyTrackerTempoCommand(0x12)]),
			Event(1.5, ChannelTarget.Physical(0), new NoteCutCommand())),
			2, root);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes[0].Commands.OfType<SetTempoRampCommand>().Single()
			.EndingTempo, Is.EqualTo(135));
		Assert.That(notes[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		double half = RampSeconds(125, 135, 6, 3);
		Assert.That(notes[1].Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(0.120 + half).Within(1e-6));
	}

	[Test]
	public void ConflictingTempoRampWhilePreviousIsActiveFailsExplicitly()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x12))), 1, root);
		timeline.Add(new RawSource(
			Event(0, ChannelTarget.Physical(0),
				new ApplyTrackerTempoCommand(0x11))), 1,
			root.FlattenedChild(physicalChannelOffset: 1));
		Assert.Throws<NotSupportedException>(() => Drain(timeline));
	}

	private static double RampSeconds(double start, double end, double ticks, double elapsedTicks)
		=> 2.5 * ticks / (end - start)
			* Math.Log((start + (end - start) * elapsedTicks / ticks) / start);

	private static DataPatternDefinition Pattern(int rows, int channels)
		=> new((ObjectId)1U, "Unused", )
		{
			RowCount = rows, ChannelCount = channels,
		};

	private static NoteEvent Event(double row, ChannelTarget target, params NoteCommand[] commands)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, commands);

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
			foreach (NoteEvent n in notes)
				yield return new RawPatternStep.Emit(n);
		}
	}

	private sealed class EagerSource(IReadOnlyList<NoteEvent> notes, double count) : IRawPatternNoteGenerator
	{
		public void GenerateRawNotes(
			SequencingContext context, INoteReceiver output, out double rowCount)
		{
			foreach (NoteEvent n in notes)
				output.Append(n);
			rowCount = count;
		}
	}
}
