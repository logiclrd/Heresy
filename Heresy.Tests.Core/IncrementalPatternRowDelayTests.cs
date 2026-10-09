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
	public void SEyRepeatsTrackerTxxSlideFromPreviousSpanEndingTempo()
	{
		DataPatternDefinition p = Pattern(2, 2);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x11));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		p.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		p.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteOff();
		AssertParity(p);

		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(p, 2, context);
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(n => n.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 130.0, 135.0, 140.0 }));
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 6.0, 6.0, 6.0 }));
		Assert.That(context.State.Tempo, Is.EqualTo(140));
	}

	[Test]
	public void S6xExtendsEachSEyTxxRampAndDelaysTheNextNote()
	{
		DataPatternDefinition p = Pattern(2, 2);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		p.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		AssertParity(p);

		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(p, 2, context);
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(n => n.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 139.0, 153.0 }));
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 8.0, 8.0 }));
		Assert.That(context.State.Tempo, Is.EqualTo(153));
	}

	[Test]
	public void SEyRepeatedTxxImmediateSetReplaysBeforeEachTxxSlide()
	{
		DataPatternDefinition p = Pattern(2, 3);
		p.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerTempoPatternEffect(0xFA));
		p.Grid.GetOrCreateCell(0, 1).Effects.Add(new TrackerTempoPatternEffect(0x11));
		p.Grid.GetOrCreateCell(0, 2).Effects.Add(new TrackerPatternDelayPatternEffect(2));
		p.Grid.GetOrCreateCell(1, 2).Note = new PatternNoteCut();
		AssertParity(p);

		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(p, 2, context);
		NoteEvent[] notes = Drain(timeline);
		SetTempoRampCommand[] ramps = notes.SelectMany(n => n.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.That(ramps.Select(r => r.EndingTempo),
			Is.EqualTo(new[] { 255.0, 255.0, 255.0 }));
		Assert.That(ramps.Select(r => r.TrackerTicks),
			Is.EqualTo(new[] { 6.0, 6.0, 6.0 }));
		Assert.That(notes.SelectMany(n => n.Commands)
			.OfType<SetTempoCommand>().Select(c => c.TicksPerDiachron),
			Is.EqualTo(new[] { 250.0, 250.0, 250.0 }));
		Assert.That(context.State.Tempo, Is.EqualTo(255.0));
	}

	[Test]
	public void WallDeadlineInsideSecondSEyTempoRampKeepsExactTickAndTime()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		double firstSpanSeconds = 2.5 * 6 / 10.0 * Math.Log(135.0 / 125.0);
		TimeSpan deadline = TimeSpan.FromSeconds(firstSpanSeconds + 0.020);
		timeline.Add(new RawSource(new NoteEvent(
			new MusicalTime(deadline, 0.0),
			ChannelTarget.Physical(0), [new NoteOffCommand()])),
			1, state.FlattenedChild(physicalChannelOffset: 4));

		IncrementalPatternTimelineStep.Emit? due = null;
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e
				&& e.Note.Commands.Any(c => c is NoteOffCommand))
				due = e;

		Assert.That(due, Is.Not.Null);
		Assert.That(due!.Note.Offset.TimeOffset, Is.EqualTo(deadline));
		double slope = 10.0 / 6.0;
		double expectedTick = 6.0 + 135.0 / slope *
			(Math.Exp(slope * 0.020 / 2.5) - 1);
		Assert.That(due.Tick, Is.EqualTo(expectedTick).Within(1e-6));
		Assert.That(state.State.Tempo, Is.EqualTo(145.0));
	}

	[Test]
	public void CancellingSEyPatternDiscardRemainingTempoRepetitions()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		long id = timeline.Add(p, 1, state);
		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? first),
			Is.True);
		Assert.That(first, Is.TypeOf<IncrementalPatternTimelineStep.Emit>());
		Assert.That(((IncrementalPatternTimelineStep.Emit)first!).Note.Commands
			.Single(), Is.EqualTo(new SetTempoRampCommand(135, 6)));

		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(timeline.IsComplete, Is.True);
		Assert.That(timeline.TryStep(out _), Is.False);
		Assert.That(state.State.Tempo, Is.EqualTo(125.0));
	}

	[Test]
	public void ConcurrentIndependentSEyAndTxxComposeInPhysicalOrder()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		timeline.Add(new RawSource(new NoteEvent(
			MusicalTime.Zero, ChannelTarget.Physical(0),
			[new ApplyTrackerTempoCommand(0x11)])),
			1, state.FlattenedChild(physicalChannelOffset: 3));
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 140.0, 150.0 }));
		Assert.That(notes.SelectMany(x => x.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.TrackerTicks),
			Is.EqualTo(new[] { 6.0, 6.0 }));
		Assert.That(state.State.Tempo, Is.EqualTo(150.0));
		Assert.That(state.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte first), Is.True);
		Assert.That(first, Is.EqualTo(0x12));
		Assert.That(state.GetPhysicalChannelState(3)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte second), Is.True);
		Assert.That(second, Is.EqualTo(0x11));
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
	public void SEyAndTxxSameCellRepeatWithEagerParityAndRememberedByte()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x12));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.SelectMany(n => n.Commands)
			.OfType<SetTempoRampCommand>().Select(x => x.EndingTempo),
			Is.EqualTo(new[] { 135.0, 145.0 }));
		Assert.That(state.State.Tempo, Is.EqualTo(145));
		Assert.That(state.GetPhysicalChannelState(0)
			.TryGetEffectParameter(EffectMemorySlot.Tempo, out byte recalled), Is.True);
		Assert.That(recalled, Is.EqualTo(0x12));
	}

	[Test]
	public void FineDelayOverridesContinuousTrackerVolumeSlideTicksPerRow()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x01));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes[0].Commands,
			Does.Contain(new SetNoteVolumeSlideCommand(-1, 9)));
		Assert.That(notes[^1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void FineVolumeAdjustmentRepeatsAtEverySEySpan()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0xF2));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		AssertParity(p);
	}

	[Test]
	public void S6xAndSEyRepeatContinuousEffectWithEffectiveRowTicks()
	{
		DataPatternDefinition p = Pattern(1, 1);
		var cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x01));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Select(x => x.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero,
				TimeSpan.FromMilliseconds(160), TimeSpan.FromMilliseconds(320) }));
		Assert.That(events[1].Commands,
			Does.Contain(new SetNoteVolumeSlideCommand(-1, 8)));
	}

	[Test]
	public void FractionalContinuousEffectRepeatsAtSameFractionOfEachDelayedSpan()
	{
		RawSource source = new(
			new NoteEvent(new MusicalTime(TimeSpan.Zero, 0),
				ChannelTarget.Physical(0), [new ApplyTrackerPatternDelayCommand(2)]),
			new NoteEvent(new MusicalTime(TimeSpan.Zero, 0.5),
				ChannelTarget.Physical(0), [new SetPitchSlideCommand(24)]));
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(source, 1, state);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(180),
				TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(360) }));
	}

	[Test]
	public void CombinedSDxQxyDelaysNewNoteBeforeStartingRetriggerTicks()
	{
		DataPatternDefinition p = Pattern(1, 1);
		PatternCell cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(2));
		cell.Effects.Add(new RetriggerPatternEffect(0x03));
		AssertParity(p);
	}

	[Test]
	public void CombinedSDxQxyAndSEyRepeatNoteAndRetriggerInEagerOrder()
	{
		DataPatternDefinition p = Pattern(1, 1);
		PatternCell cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(2));
		cell.Effects.Add(new RetriggerPatternEffect(0x03));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		AssertParity(p);

		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, 1, state);
		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset),
			Is.EqualTo(new[] { 40, 100, 160, 160, 220 }
				.Select(x => TimeSpan.FromMilliseconds(x))));
		Assert.That(notes.Select(n => n.Commands[0].GetType()),
			Is.EqualTo(new[] {
				typeof(StartNoteCommand), typeof(RetriggerCurrentVoiceCommand),
				typeof(StartNoteCommand), typeof(RetriggerCurrentVoiceCommand),
				typeof(RetriggerCurrentVoiceCommand),
			}));
	}

	[Test]
	public void CombinedSDxQxyAndS6xRetainOriginalSpanAndRepetitionTiming()
	{
		DataPatternDefinition p = Pattern(1, 1);
		PatternCell cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(8));
		cell.Effects.Add(new RetriggerPatternEffect(0x02));
		AssertParity(p);
	}

	[Test]
	public void OutOfSpanCombinedSDxQxyDoesNotStartOrRetrigger()
	{
		DataPatternDefinition p = Pattern(2, 1);
		PatternCell cell = p.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(6));
		cell.Effects.Add(new RetriggerPatternEffect(0x93));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		p.Grid.GetOrCreateCell(1, 0).Effects.Add(new RetriggerPatternEffect(0));
		AssertParity(p);
		SequencingContext state = new();
		using IncrementalPatternTimeline timeline = new(state);
		timeline.Add(p, p.RowCount, state);
		Drain(timeline);
		Assert.That(state.GetPhysicalChannelState(0).TryGetEffectParameter(
			EffectMemorySlot.Retrigger, out byte parameter), Is.True);
		Assert.That(parameter, Is.EqualTo(0x93));
	}

	[Test]
	public void DelayedRetriggerCountdownAndQ00MemoryCarryToFollowingRow()
	{
		DataPatternDefinition p = Pattern(2, 1);
		PatternCell first = p.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote((ObjectId)10U);
		first.Effects.Add(new TrackerNoteDelayPatternEffect(2));
		first.Effects.Add(new RetriggerPatternEffect(0x03));
		p.Grid.GetOrCreateCell(1, 0).Effects.Add(new RetriggerPatternEffect(0));
		AssertParity(p);
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
