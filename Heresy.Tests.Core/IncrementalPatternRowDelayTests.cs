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
public sealed class IncrementalPatternRowDelayTests
{
	[Test]
	public void S6xAccumulatesByChannelAndLeavesFractionalEventAtOriginalTick()
	{
		DataPatternDefinition p = Pattern(2, 2);
		p.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		p.Grid.GetOrCreateCell(0, 1).Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		p.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		p.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();

		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, p.RowCount, state);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(220) }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(340)));
	}

	[Test]
	public void SEyRepeatsRowSpanWithoutRetriggeringOrdinaryNotes()
	{
		DataPatternDefinition p = Pattern(2, 1);
		var first = p.Grid.GetOrCreateCell(0, 0);
		first.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		first.Note = new PatternNoteCut();
		p.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, p.RowCount, state);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(360) }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
	}

	[Test]
	public void S6xAndSEyMultiplyTheCapturedSpanAndFinishAtExtendedBoundary()
	{
		DataPatternDefinition p = Pattern(2, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		p.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, p.RowCount, state);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(320)));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(440)));
	}

	[Test]
	public void SEyFirstPhysicalChannelWinsIrrespectiveOfEmissionCreationOrder()
	{
		DataPatternDefinition p = Pattern(1, 2);
		p.Grid.GetOrCreateCell(0, 1).Effects.Add(new TrackerPatternDelayPatternEffect(3));
		p.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerPatternDelayPatternEffect(1));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		Drain(timeline);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void DelayedParentRowLetsChildTempoChangeItsActualWallDuration()
	{
		DataPatternDefinition parent = Pattern(2, 1);
		parent.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(2));
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(parent, 2, root);
		timeline.Add(new RawSource(new NoteEvent(
			new MusicalTime(TimeSpan.Zero, 0), ChannelTarget.Global,
			[new SetTempoCommand(250)])), 1,
			root.FlattenedChild(physicalChannelOffset: 3));
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Single(n => n.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void S6xExtendsTxxRampAndPreservesFractionalTickTiming()
	{
		DataPatternDefinition p = Pattern(1, 2);
		p.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		p.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerTempoPatternEffect(0x11));
		p.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		SequencingContext eager = new();
		NoteScheduleBuilder builder = new();
		PatternNoteProcessor.GenerateNotes(p, eager, builder, out TimeSpan expected);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().Single().EndingTempo, Is.EqualTo(132));
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(expected.TotalSeconds).Within(1e-6));
		Assert.That(state.State.Tempo, Is.EqualTo(132));
	}

	[Test]
	public void ContinuousPitchAndVolumeSlidesRepeatAndClearAtLastSEyBoundary()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new PitchSlidePatternEffect(48));
		cell.Effects.Add(new NoteVolumeSlidePatternEffect(-4));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.Zero, TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(360) }));
		Assert.That(notes[^1].Commands, Does.Contain(new ClearPitchSlideCommand()));
		Assert.That(notes[^1].Commands, Does.Contain(new ClearNoteVolumeSlideCommand()));
	}

	[Test]
	public void S6xAndSameRowSpeedChangeUseNewRowSpeedWithoutChangingOtherCursor()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetSpeedPatternEffect(3));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		Drain(timeline);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
		Assert.That(state.State.Speed, Is.EqualTo(3));
	}

	[Test]
	public void IncompatibleSEyAndTxxCombinationFailsBeforeChangingTempoMemory()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		Assert.Throws<NotSupportedException>(() => timeline.TryStep(out _));
		Assert.That(state.State.Tempo, Is.EqualTo(125));
		Assert.That(state.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out _), Is.False);
	}

	private static DataPatternDefinition Pattern(int rows, int channels)
		=> new((ObjectId)1U, "Delay parity") { RowCount = rows, ChannelCount = channels };

	private static void AssertParity(DataPatternDefinition pattern)
	{
		SequencingContext eager = new();
		NoteScheduleBuilder old = new();
		PatternNoteProcessor.GenerateNotes(pattern, eager, old, out TimeSpan duration);
		SequencingContext incremental = new();
		using IncrementalPatternTimeline timeline = new(incremental);
		timeline.Add(pattern, pattern.RowCount, incremental);
		NoteEvent[] expected = old.Freeze().ToArray();
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < expected.Length; i++)
		{
			Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target));
			Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands));
			Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds).Within(1e-6));
		}
		Assert.That(timeline.Elapsed.TotalSeconds,
			Is.EqualTo(duration.TotalSeconds).Within(1e-6));
	}

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
