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
	public void RecycledSlotKeepsOldGenerationOnDeferredWallDeadline()
	{
		var oldTarget = ChannelTarget.Virtual(new VirtualChannelIdentity(7, 1));
		var newTarget = ChannelTarget.Virtual(new VirtualChannelIdentity(7, 2));
		var legacy = ChannelTarget.Virtual(7);
		Assert.That(new[] { oldTarget, newTarget, legacy }.Distinct().Count(), Is.EqualTo(3));
		RawSource source = new(
			At(0, oldTarget, new StartNoteCommand((ObjectId)90U)),
			new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(85), 0),
				oldTarget, [new NoteCutCommand()]),
			At(0.5, newTarget, new StartNoteCommand((ObjectId)91U)));
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long owner = timeline.Add(source, 1, context);
		var actual = Drain(timeline);
		Assert.Multiple(() =>
		{
			Assert.That(actual.Select(x => x.InvocationId), Is.All.EqualTo(owner));
			Assert.That(actual.Select(x => x.Note.Target),
				Is.EqualTo(new[] { oldTarget, newTarget, oldTarget }));
			Assert.That(actual.Select(x => x.Note.Offset.TimeOffset),
				Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(60),
					TimeSpan.FromMilliseconds(85) }));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ReleaseWaitsForEveryAcceptedEventIncludingDeadlineBeyondFinalRow(bool explicitRelease)
	{
		var old = new VirtualChannelIdentity(0, 1);
		var replacement = new VirtualChannelIdentity(0, 2);
		var target = ChannelTarget.Virtual(old);
		List<RawPatternStep> steps = [
			new RawPatternStep.Emit(At(0, target, new StartNoteCommand((ObjectId)90U))),
			new RawPatternStep.Emit(new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(200), 0),
				target, [new NoteCutCommand()])),
		];
		if (explicitRelease) steps.Add(new RawPatternStep.ReleaseVirtualChannel(old));
		steps.Add(new RawPatternStep.Emit(At(0.5, ChannelTarget.Virtual(replacement),
			new StartNoteCommand((ObjectId)91U))));
		steps.Add(new RawPatternStep.ReleaseVirtualChannel(replacement));
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long owner = timeline.Add(new StepSource(steps), 1, context);
		List<(long Owner, VirtualChannelIdentity Identity, TimeSpan Time)> retired = [];
		timeline.VirtualChannelFinalUse += (id, identity) => retired.Add((id, identity, timeline.Elapsed));
		while (timeline.TryStep(out var step))
		{
			if (step is IncrementalPatternTimelineStep.Emit emit
				&& emit.Note.Target.VirtualIdentity == old
				&& emit.Time == TimeSpan.Zero)
				Assert.That(retired, Is.Empty);
			if (timeline.Elapsed < TimeSpan.FromMilliseconds(200))
				Assert.That(retired.Any(x => x.Identity == old), Is.False);
		}
		Assert.That(retired, Is.EqualTo(new[] {
			(owner, replacement, TimeSpan.FromMilliseconds(60)),
			(owner, old, TimeSpan.FromMilliseconds(200)),
		}));
	}

	[Test]
	public void CancellationDiscardsDeadlineAndClosesOnlyCanceledGeneration()
	{
		var id = new VirtualChannelIdentity(0, 1);
		var target = ChannelTarget.Virtual(id);
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long owner = timeline.Add(new StepSource([
			new RawPatternStep.Emit(At(0, target, new StartNoteCommand((ObjectId)90U))),
			new RawPatternStep.Emit(new NoteEvent(new MusicalTime(TimeSpan.FromMilliseconds(300), 0),
				target, [new NoteCutCommand()])),
			new RawPatternStep.ReleaseVirtualChannel(id),
		]), 1, context);
		List<(long, VirtualChannelIdentity)> retired = [];
		timeline.VirtualChannelFinalUse += (o, i) => retired.Add((o, i));
		while (timeline.TryStep(out var step))
			if (step is IncrementalPatternTimelineStep.Advance) break;
		Assert.That(retired, Is.Empty);
		Assert.That(timeline.Cancel(owner), Is.True);
		Assert.That(retired, Is.EqualTo(new[] { (owner, id) }));
		Assert.That(Drain(timeline), Is.Empty);
		Assert.That(timeline.Cancel(owner), Is.False);
	}

	[Test]
	public void StartingRowSkipsUnmaterializedGenerationWithoutRetirementTombstone()
	{
		var skipped = new VirtualChannelIdentity(0, 1);
		var kept = new VirtualChannelIdentity(0, 2);
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long owner = timeline.Add(new StepSource([
			new RawPatternStep.Emit(At(0, ChannelTarget.Virtual(skipped), new StartNoteCommand((ObjectId)90U))),
			new RawPatternStep.ReleaseVirtualChannel(skipped),
			new RawPatternStep.Emit(At(1, ChannelTarget.Virtual(kept), new StartNoteCommand((ObjectId)91U))),
			new RawPatternStep.ReleaseVirtualChannel(kept),
		]), 2, context, startRow: 1);
		List<(long, VirtualChannelIdentity)> retired = [];
		timeline.VirtualChannelFinalUse += (o, i) => retired.Add((o, i));
		Assert.That(Drain(timeline).Select(e => e.Note.Target.VirtualIdentity), Is.EqualTo(new[] { kept }));
		Assert.That(retired, Is.EqualTo(new[] { (owner, kept) }));
	}

	[Test]
	public void LongRunningDeferredGenerationsRetireWithoutHistoricalEntries()
	{
		const int cycles = 4096;
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		long owner = timeline.Add(new RecyclingSource(cycles), cycles, context);
		int retired = 0, emitted = 0, peak = 0;
		timeline.VirtualChannelFinalUse += (id, identity) =>
		{
			if (id != owner || identity.Slot != 0
				|| identity.Generation != (ulong)retired + 1)
				Assert.Fail("Missing, duplicated or misordered final-use notification.");
			double expectedMs = retired * 120.0 + 180.0;
			if (Math.Abs(timeline.Elapsed.TotalMilliseconds - expectedMs) > 0.001)
				Assert.Fail("Final use fired before or after the delayed cut deadline.");
			retired++;
		};
		while (timeline.TryStep(out var step))
		{
			if (step is IncrementalPatternTimelineStep.Emit) emitted++;
			peak = Math.Max(peak, timeline.RetainedVirtualIdentityCount);
			if (timeline.RetainedVirtualIdentityCount > 4)
				Assert.Fail("Retirement entries grew beyond the overlap horizon.");
		}
		Assert.Multiple(() =>
		{
			Assert.That(emitted, Is.EqualTo(cycles * 2));
			Assert.That(retired, Is.EqualTo(cycles));
			Assert.That(peak, Is.InRange(2, 4));
			Assert.That(timeline.RetainedVirtualIdentityCount, Is.Zero);
			Assert.That(timeline.IsComplete, Is.True);
		});
	}

	private sealed class RecyclingSource(int cycles) : IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			for (int row = 0; row < cycles; row++)
			{
				VirtualChannelIdentity id = new(0, (ulong)row + 1);
				ChannelTarget target = ChannelTarget.Virtual(id);
				yield return new RawPatternStep.Emit(At(row, target,
					new StartNoteCommand((ObjectId)90U)));
				yield return new RawPatternStep.Emit(new NoteEvent(
					new MusicalTime(TimeSpan.FromMilliseconds(180), row),
					target, [new NoteCutCommand()]));
				yield return new RawPatternStep.ReleaseVirtualChannel(id);
			}
			yield return new RawPatternStep.Advance(cycles);
		}
	}

	private sealed class StepSource(IReadOnlyList<RawPatternStep> steps)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
			=> steps;
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
