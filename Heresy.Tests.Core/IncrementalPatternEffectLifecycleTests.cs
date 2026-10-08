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
public sealed class IncrementalPatternEffectLifecycleTests
{
	[Test]
	public void RowScopedPitchAndVolumeSlidesEndAtTheirOwnRowBoundary()
	{
		SequencingContext context = new();
		DataPatternDefinition pattern = new((ObjectId)1U, "Slides")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
		first.Effects.Add(new PitchSlidePatternEffect(48));
		first.Effects.Add(new NoteVolumeSlidePatternEffect(-4));
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();

		NoteScheduleBuilder eager = new();
		PatternNoteProcessor.GenerateNotes(pattern,
			new SequencingContext { ResolvePatternSourcesAtRowTime = true },
			eager, out TimeSpan eagerDuration);

		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(pattern, pattern.RowCount, context);
		NoteEvent[] actual = Drain(timeline);

		NoteEvent[] baseline = eager.Freeze().ToArray();
		Assert.That(actual.Length, Is.EqualTo(baseline.Length));
		for (int i = 0; i < baseline.Length; i++)
		{
			Assert.That(actual[i].Offset.TimeOffset,
				Is.EqualTo(baseline[i].Offset.TimeOffset));
			Assert.That(actual[i].Commands, Is.EqualTo(baseline[i].Commands));
		}
		Assert.That(actual[0].Commands, Does.Contain(new SetPitchSlideCommand(48)));
		Assert.That(actual[0].Commands, Does.Contain(new SetNoteVolumeSlideCommand(-4)));
		Assert.That(actual[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(actual[1].Commands, Does.Contain(new ClearPitchSlideCommand()));
		Assert.That(actual[1].Commands, Does.Contain(new ClearNoteVolumeSlideCommand()));
		Assert.That(timeline.Elapsed, Is.EqualTo(eagerDuration));
	}

	[Test]
	public void TrackerDxxRecallsSharedVolumeSlideMemoryOnLaterRow()
	{
		SequencingContext root = new();
		DataPatternDefinition pattern = new((ObjectId)1U, "Dxy")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x30));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x00));

		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(pattern, pattern.RowCount, root);
		NoteEvent[] events = Drain(timeline);
		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240),
			}));
		Assert.That(events[0].Commands, Does.Contain(new SetNoteVolumeSlideCommand(3)));
		Assert.That(events[1].Commands, Does.Contain(new ClearNoteVolumeSlideCommand()));
		Assert.That(events[2].Commands, Does.Contain(new SetNoteVolumeSlideCommand(3)));
		Assert.That(events[3].Commands, Does.Contain(new ClearNoteVolumeSlideCommand()));
	}

	[Test]
	public void LaterChildTempoChangeMovesPendingSlideCleanupWallTime()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawStream(
			At(0, 0, new SetPitchSlideCommand(24)),
			At(0.5, 0, new NoteCutCommand())), 1, root);
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit emit)
				continue;
			events.Add(emit.Note);
			if (emit.Note.Commands.Any(c => c is NoteCutCommand))
				timeline.Add(new RawStream(
					At(0, ChannelTarget.Global, new SetTempoCommand(250))),
					1, root.FlattenedChild());
		}

		Assert.That(events.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(90),
			}));
		Assert.That(events[^1].Commands, Does.Contain(new ClearPitchSlideCommand()));
		Assert.That(root.State.Tempo, Is.EqualTo(250));
	}

	[Test]
	public void PositiveWallOffsetDoesNotResolveBeforeItsDeadlineOrMoveAfterTempoChange()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawStream(
			At(0.5, TimeSpan.FromMilliseconds(100), 0, new NoteOffCommand()),
			At(1.0, 0, new NoteCutCommand())), 2, root);
		timeline.Add(new RawStream(
			At(1.0, ChannelTarget.Global, new SetTempoCommand(250))), 2,
			root.FlattenedChild(physicalChannelOffset: 1));

		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(160),
			}));
		Assert.That(actual[0].Commands.Single(), Is.TypeOf<NoteCutCommand>());
		Assert.That(actual[2].Commands.Single(), Is.TypeOf<NoteOffCommand>());
	}

	[Test]
	public void CancelledInvocationDropsItsOutstandingWallDeadline()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long id = timeline.Add(new RawStream(
			At(0, TimeSpan.FromMilliseconds(500), 0, new NoteOffCommand()),
			At(0.5, 0, new NoteCutCommand())), 1, root);
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Emit)
				break;
		}

		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(timeline.TryStep(out _), Is.False);
	}

	[Test]
	public void UnsupportedDeferredGlobalTempoChangesRemainExplicit()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawStream(
			At(0, TimeSpan.FromMilliseconds(20),
				ChannelTarget.Global, new SetTempoCommand(250))), 1, root);
		Assert.Throws<NotSupportedException>(() => timeline.TryStep(out _));
	}

	private static NoteEvent At(double row, int channel, NoteCommand command)
		=> At(row, TimeSpan.Zero, ChannelTarget.Physical(channel), command);

	private static NoteEvent At(double row, ChannelTarget target, NoteCommand command)
		=> At(row, TimeSpan.Zero, target, command);

	private static NoteEvent At(double row, TimeSpan time, int channel, NoteCommand command)
		=> At(row, time, ChannelTarget.Physical(channel), command);

	private static NoteEvent At(
		double row, TimeSpan time, ChannelTarget target, NoteCommand command)
		=> new(new MusicalTime(time, row), target, [command]);

	private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				events.Add(emit.Note);
		return events.ToArray();
	}

	private sealed class RawStream(params NoteEvent[] events)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in events)
				yield return new RawPatternStep.Emit(note);
		}
	}
}
