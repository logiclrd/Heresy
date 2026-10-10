using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

/// <summary>
/// Cross-rate Txx contributors resolve into one shared tracker Tempo without
/// changing their independently captured local tick spans. No source is
/// expanded beyond the next pending raw step.
/// </summary>
[TestFixture]
public sealed class IncrementalCrossRateTempoTests
{
	[Test]
	public void SimultaneousScaledAndUnscaledSlidesEndAtTheirOwnSharedTickBoundaries()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		// Start scaled first to prove arbitration is not cursor creation order.
		SequencingContext fast = root.FlattenedChild(
			playbackSpeedMultiplier: 2.0, physicalChannelOffset: 3);
		timeline.Add(new RawSource(At(0, 0, new ApplyTrackerTempoCommand(0x12))),
			1, fast);
		timeline.Add(new RawSource(At(0, 0, new ApplyTrackerTempoCommand(0x11))),
			1, root);

		NoteEvent[] events = Drain(timeline);
		SetTempoRampCommand[] ramps = events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(ramps.Select(r => r.EndingTempo),
				Is.EqualTo(new[] { 137.5, 140.0 }).Within(1e-8));
			Assert.That(ramps.Select(r => r.TrackerTicks),
				Is.EqualTo(new[] { 3.0, 3.0 }));
			Assert.That(root.State.Tempo, Is.EqualTo(140).Within(1e-8));
			Assert.That(timeline.Tick, Is.EqualTo(6).Within(1e-8));
			Assert.That(timeline.Elapsed.TotalSeconds,
				Is.EqualTo(RampSeconds(125, 137.5, 3)
					+ RampSeconds(137.5, 140, 3)).Within(1e-6));
		});
	}

	[Test]
	public void ScaledSEyRepeatsFollowLocalRowsNotUnscaledCompetitors()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		SequencingContext fast = root.FlattenedChild(
			playbackSpeedMultiplier: 2.0, physicalChannelOffset: 3);
		long scaled = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(2)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12)),
			At(1, 0, new NoteOffCommand())), 2, fast);
		long unscaled = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x11)),
			At(1, 0, new NoteCutCommand())), 2, root);
		NoteEvent[] events = Drain(timeline);
		SetTempoRampCommand[] ramps = events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(ramps.Select(r => r.EndingTempo),
				Is.EqualTo(new[] { 137.5, 150.0, 160.0 }).Within(1e-8));
			Assert.That(ramps.Select(r => r.TrackerTicks),
				Is.EqualTo(new[] { 3.0, 3.0, 3.0 }));
			Assert.That(root.State.Tempo, Is.EqualTo(160.0).Within(1e-8));
			Assert.That(timeline.HasOutstandingWork(scaled), Is.False);
			Assert.That(timeline.HasOutstandingWork(unscaled), Is.False);
			Assert.That(events.Single(e => e.Commands.Any(c => c is NoteOffCommand))
				.Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(RampSeconds(125, 137.5, 3)
					+ RampSeconds(137.5, 150, 3)
					+ RampSeconds(150, 160, 3)).Within(1e-6));
		});
	}

	[Test]
	public void CancelScaledContributorKeepsOtherSlideAtItsInstantaneousTempo()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		SequencingContext fast = root.FlattenedChild(
			playbackSpeedMultiplier: 2.0, physicalChannelOffset: 3);
		long scaled = timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, fast);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x11)),
			At(1, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawSource(
			At(0.25, 0, new NoteOffCommand())), 1,
			root.FlattenedChild(physicalChannelOffset: 5));
		bool cancelled = false;
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			notes.Add(emit.Note);
			if (emit.Note.Commands.Any(c => c is NoteOffCommand))
			{
				Assert.That(emit.Tick, Is.EqualTo(1.5).Within(1e-8));
				Assert.That(root.State.Tempo, Is.EqualTo(131.25).Within(1e-6));
				Assert.That(timeline.Cancel(scaled), Is.True);
				cancelled = true;
			}
		}
		Assert.Multiple(() =>
		{
			Assert.That(cancelled, Is.True);
			Assert.That(root.State.Tempo, Is.EqualTo(135).Within(1e-6));
			Assert.That(notes.Single(n => n.Commands.Any(c => c is NoteCutCommand))
				.Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(RampSeconds(125, 131.25, 1.5)
					+ RampSeconds(131.25, 135, 4.5)).Within(1e-6));
		});
	}

	[TestCase(true, 33.0, 35.0)]
	[TestCase(false, 34.5, 32.0)]
	public void OpposingCrossRateSlidesClampInMappedPhysicalChannelOrder(
		bool scaledDownFirst, double firstBoundaryTempo, double endingTempo)
	{
		SequencingContext root = new();
		root.State.Tempo = 32;
		using IncrementalPatternTimeline timeline = new(root);
		// The scaled voice applies five local changes by shared tick 3;
		// the unscaled voice applies five through shared tick 6.
		// Where two local ticks coincide, mapped physical order decides
		// who clamps to the lower IT Tempo limit before the other acts.
		SequencingContext fast = root.FlattenedChild(
			playbackSpeedMultiplier: 2.0,
			physicalChannelOffset: scaledDownFirst ? 0 : 3);
		SequencingContext slow = root.FlattenedChild(
			physicalChannelOffset: scaledDownFirst ? 3 : 0);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(scaledDownFirst ? (byte)0x01 : (byte)0x11))),
			1, fast);
		timeline.Add(new RawSource(At(0, 0,
			new ApplyTrackerTempoCommand(scaledDownFirst ? (byte)0x11 : (byte)0x01))),
			1, slow);
		NoteEvent[] events = Drain(timeline);
		SetTempoRampCommand[] ramps = events.SelectMany(e => e.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(ramps.Select(r => r.EndingTempo),
				Is.EqualTo(new[] { firstBoundaryTempo, endingTempo }).Within(1e-8));
			Assert.That(root.State.Tempo, Is.EqualTo(endingTempo).Within(1e-8));
		});
	}

	[Test]
	public void FractionalScaledSEyKeepsAnalyticWallDeadlineInLaterTempoSegment()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		SequencingContext scaled = root.FlattenedChild(
			playbackSpeedMultiplier: 1.5, physicalChannelOffset: 2);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerPatternDelayCommand(1)),
			At(0, 0, new ApplyTrackerTempoCommand(0x12))), 1, scaled);
		timeline.Add(new RawSource(
			At(0, 0, new ApplyTrackerTempoCommand(0x11))), 1, root);
		const double atFour = 125 + 40.0 / 3.0;
		const double atSix = 145;
		const double atEight = 150;
		double deadlineSeconds = RampSeconds(125, atFour, 4)
			+ RampSeconds(atFour, atFour + 10.0 / 3.0, 1);
		TimeSpan deadline = TimeSpan.FromSeconds(deadlineSeconds);
		timeline.Add(new RawSource(new NoteEvent(
			new MusicalTime(deadline, 0), ChannelTarget.Physical(0),
			[new NoteOffCommand()])), 1,
			root.FlattenedChild(physicalChannelOffset: 5));
		List<IncrementalPatternTimelineStep.Emit> emitted = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				emitted.Add(emit);
		double? dueTick = emitted.Single(e => e.Note.Commands
			.Any(c => c is NoteOffCommand)).Tick;
		SetTempoRampCommand[] ramps = emitted
			.SelectMany(e => e.Note.Commands)
			.OfType<SetTempoRampCommand>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(ramps.Select(r => r.EndingTempo),
				Is.EqualTo(new[] { atFour, atSix, atEight }).Within(1e-8));
			Assert.That(ramps.Select(r => r.TrackerTicks),
				Is.EqualTo(new[] { 4.0, 2.0, 2.0 }));
			Assert.That(dueTick, Is.EqualTo(5.0).Within(1e-5));
			Assert.That(root.State.Tempo, Is.EqualTo(atEight).Within(1e-8));
		});
	}

	private static double RampSeconds(double initial, double ending, double ticks)
		=> Math.Abs(ending - initial) < 1e-9
			? 2.5 * ticks / initial
			: 2.5 * ticks / (ending - initial) * Math.Log(ending / initial);

	private static NoteEvent At(double row, int channel, NoteCommand command)
		=> new(new MusicalTime(TimeSpan.Zero, row),
			ChannelTarget.Physical(channel), [command]);

	private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				notes.Add(emit.Note);
		return notes.ToArray();
	}

	private sealed class RawSource(params NoteEvent[] notes)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in notes)
				yield return new RawPatternStep.Emit(note);
		}
	}
}
