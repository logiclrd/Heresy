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
public sealed class IncrementalVirtualTargetTests
{
	[Test]
	public void VirtualAndBroadcastNotesMatchEagerTimingAndTargets()
	{
		RawSource source = new(
			At(0, ChannelTarget.Virtual(17), new StartNoteCommand((ObjectId)90U)),
			At(0.25, ChannelTarget.AllVirtualInScope, new NoteOffCommand()),
			At(0.5, ChannelTarget.Virtual(17), new NoteCutCommand()),
			At(0.75, ChannelTarget.AllVirtual, new NoteOffCommand()));
		NoteScheduleBuilder eagerBuilder = new();
		PatternNoteProcessor.GenerateNotes(
			new EagerSource(source.Events, 1), new SequencingContext(),
			eagerBuilder, out TimeSpan expectedDuration);

		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(source, 1, context);
		IncrementalPatternTimelineStep.Emit[] actual = Drain(timeline);
		NoteEvent[] expected = eagerBuilder.Freeze().ToArray();

		Assert.That(actual.Length, Is.EqualTo(expected.Length));
		for (int i = 0; i < expected.Length; i++)
		{
			Assert.That(actual[i].Note.Target, Is.EqualTo(expected[i].Target));
			Assert.That(actual[i].Note.Commands, Is.EqualTo(expected[i].Commands));
			Assert.That(actual[i].Note.Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds)
					.Within(1e-6));
		}
		Assert.That(actual.Select(x => x.Note.Target),
			Is.EqualTo(new[] {
				ChannelTarget.Virtual(17),
				ChannelTarget.AllVirtualInScope,
				ChannelTarget.Virtual(17),
				ChannelTarget.AllVirtual,
			}));
		Assert.That(timeline.Elapsed, Is.EqualTo(expectedDuration));
	}

	[Test]
	public void FlattenedSiblingsKeepTheirVirtualIdentityAndSeparateOwners()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long first = timeline.Add(new RawSource(
			At(0, ChannelTarget.Virtual(7),
				new StartNoteCommand((ObjectId)10U)),
			At(0.5, ChannelTarget.AllVirtualInScope, new NoteOffCommand())),
			1, root.FlattenedChild(physicalChannelOffset: 1));
		long second = timeline.Add(new RawSource(
			At(0.25, ChannelTarget.Virtual(7),
				new StartNoteCommand((ObjectId)11U)),
			At(0.75, ChannelTarget.AllVirtualInScope, new NoteCutCommand())),
			1, root.FlattenedChild(physicalChannelOffset: 5));

		var notes = Drain(timeline);
		Assert.That(notes.Select(x => x.InvocationId),
			Is.EqualTo(new[] { first, second, first, second }));
		Assert.That(notes.Select(x => x.Note.Target),
			Is.EqualTo(new[] {
				ChannelTarget.Virtual(7),
				ChannelTarget.Virtual(7),
				ChannelTarget.AllVirtualInScope,
				ChannelTarget.AllVirtualInScope,
			}));
		Assert.That(notes.Select(x => x.Note.Offset.TimeOffset),
			Is.EqualTo(new[] {
				TimeSpan.Zero,
				TimeSpan.FromMilliseconds(30),
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(90),
			}));
	}

	[Test]
	public void VirtualPositiveWallDeadlineUsesSharedClockAndCanBeCancelled()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		long delayed = timeline.Add(new RawSource(
			new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(85), 0.5),
				ChannelTarget.Virtual(9),
				[new StartNoteCommand((ObjectId)90U)])), 1, root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Global,
			new SetTempoCommand(250))), 1,
			root.FlattenedChild(physicalChannelOffset: 3));
		var notes = Drain(timeline);
		Assert.That(notes.Select(x => x.Note.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero,
				TimeSpan.FromMilliseconds(115) }));
		Assert.That(notes[^1].InvocationId, Is.EqualTo(delayed));
		Assert.That(notes[^1].Note.Target, Is.EqualTo(ChannelTarget.Virtual(9)));

		SequencingContext cancelledRoot = new();
		using IncrementalPatternTimeline cancelled = new(cancelledRoot);
		long owner = cancelled.Add(new RawSource(
			new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(160), 0),
				ChannelTarget.AllVirtual, [new NoteCutCommand()])),
			1, cancelledRoot);
		while (cancelled.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is IncrementalPatternTimelineStep.Advance)
				break;
		}
		Assert.That(cancelled.Cancel(owner), Is.True);
		Assert.That(Drain(cancelled), Is.Empty);
	}

	[Test]
	public void TrackerMemoryEffectsStillRequirePhysicalTargets()
	{
		SequencingContext root = new();
		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(new RawSource(At(0, ChannelTarget.Virtual(1),
			new ApplyRetriggerCommand(0x03))), 1, root);
		Assert.Throws<NotSupportedException>(() => Drain(timeline));
	}

	private static NoteEvent At(
		double row, ChannelTarget target, params NoteCommand[] commands)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, commands);

	private static IncrementalPatternTimelineStep.Emit[] Drain(
		IncrementalPatternTimeline timeline)
	{
		List<IncrementalPatternTimelineStep.Emit> results = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				results.Add(emit);
		return results.ToArray();
	}

	private sealed class RawSource(params NoteEvent[] events)
		: IIncrementalRawPatternNoteGenerator
	{
		public IReadOnlyList<NoteEvent> Events { get; } = events;
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in Events)
				yield return new RawPatternStep.Emit(note);
		}
	}

	private sealed class EagerSource(
		IReadOnlyList<NoteEvent> events, double count)
		: IRawPatternNoteGenerator
	{
		public void GenerateRawNotes(
			SequencingContext context, INoteReceiver output,
			out double rowCount)
		{
			foreach (NoteEvent note in events)
				output.Append(note);
			rowCount = count;
		}
	}
}
