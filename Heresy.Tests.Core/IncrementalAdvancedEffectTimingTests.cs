using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

/// <summary>
/// Lazy shared-tick arbitration among future wall deadlines, remembered
/// commands, global row-start timing and tracker-effect clocks. These tests
/// never pre-enumerate notes from future song orders.
/// </summary>
[TestFixture]
public sealed class IncrementalAdvancedEffectTimingTests
{
    [Test]
    public void NegativeWallOffsetRejectsBeforeTouchingFutureRowSourceOrTempo()
    {
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(new RawSource(
            At(1, TimeSpan.FromMilliseconds(-5), 0,
                new StartNoteCommand((ObjectId)7U)),
            At(1, ChannelTarget.Global, new SetTempoCommand(250))), 2, root);

        Assert.That(() => Drain(timeline),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("negative wall-time offsets"));
        Assert.Multiple(() =>
        {
            Assert.That(root.State.Tempo, Is.EqualTo(125));
            Assert.That(root.GetPhysicalChannelState(0).CurrentSourceId,
                Is.EqualTo(ObjectId.None));
        });
    }

    [Test]
    public void DeferredGlobalTempoAndSpeedInSameEventRetimesOnlyFutureTicks()
    {
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(new RawSource(
            At(0, TimeSpan.FromMilliseconds(20), ChannelTarget.Global,
                new SetTempoCommand(250), new SetSpeedCommand(3)),
            At(1.5, 0, new NoteCutCommand())), 2, root);

        NoteEvent[] events = Drain(timeline);
        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Length.EqualTo(2));
            Assert.That(events[0].Offset.TimeOffset,
                Is.EqualTo(TimeSpan.FromMilliseconds(120)));
            Assert.That(events[0].Commands,
                Is.EqualTo(new NoteCommand[]
                {
                    new SetTempoCommand(250),
                    new SetSpeedCommand(3),
                }));
            Assert.That(events[1].Offset.TimeOffset,
                Is.EqualTo(TimeSpan.FromMilliseconds(150)),
                "Row one must retain the speed captured before the deferred " +
                "global change; only its Tempo changes at the boundary.");
            Assert.That(root.State.Tempo, Is.EqualTo(250));
            Assert.That(root.State.Speed, Is.EqualTo(3));
        });
    }

    [Test]
    public void DeferredGlobalTempoWinsSameBoundaryAgainstTrackerTxxSlide()
    {
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(new RawSource(
            At(0, TimeSpan.FromMilliseconds(10), ChannelTarget.Global,
                new SetTempoCommand(200))), 2, root);
        timeline.Add(new RawSource(
            At(1, 0, new ApplyTrackerTempoCommand(0x11)),
            At(1.5, 0, new NoteCutCommand())), 2,
            root.FlattenedChild(physicalChannelOffset: 3));

        NoteEvent[] notes = Drain(timeline);
        NoteEvent[] timing = notes.Where(n => n.Commands.Any(c =>
            c is SetTempoCommand or SetTempoRampCommand)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(timing, Has.Length.EqualTo(2));
            Assert.That(timing[0].Commands,
                Is.EqualTo(new NoteCommand[] { new SetTempoCommand(200) }));
            Assert.That(timing[1].Commands,
                Is.EqualTo(new NoteCommand[] { new SetTempoRampCommand(205, 6) }));
            Assert.That(timing.Select(n => n.Offset.TimeOffset),
                Is.All.EqualTo(TimeSpan.FromMilliseconds(120)));
            Assert.That(root.State.Tempo, Is.EqualTo(205));
            Assert.That(root.GetPhysicalChannelState(3)
                .TryGetEffectParameter(EffectMemorySlot.Tempo, out byte memory),
                Is.True);
            Assert.That(memory, Is.EqualTo(0x11));
        });
    }

    [Test]
    public void LaterAuthoredWallNoteCanExecuteFirstWithoutCommittingFutureSourceMemory()
    {
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(new RawSource(
            At(0, 0, new SelectPatternSourceCommand((ObjectId)7U)),
            At(0.2, TimeSpan.FromMilliseconds(120), 0,
                new StartNoteCommand(ObjectId.None)),
            At(0.7, TimeSpan.FromMilliseconds(20), 0,
                new StartNoteCommand(ObjectId.None)),
            At(1, 0, new SelectPatternSourceCommand((ObjectId)8U))), 2, root);

        NoteEvent[] starts = Drain(timeline);
        Assert.Multiple(() =>
        {
            Assert.That(starts, Has.Length.EqualTo(2));
            Assert.That(starts.Select(n => n.Offset.TimeOffset),
                Is.EqualTo(new[]
                {
                    TimeSpan.FromMilliseconds(104),
                    TimeSpan.FromMilliseconds(144),
                }));
            Assert.That(starts.SelectMany(n => n.Commands)
                .OfType<StartNoteCommand>().Select(n => n.SourceId),
                Is.EqualTo(new[] { (ObjectId)7U, (ObjectId)8U }));
        });
    }

    [Test]
    public void CancelPendingWallNoteDoesNotCommitItsRememberedSampleOffset()
    {
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        long pending = timeline.Add(new RawSource(
            At(0, TimeSpan.FromMilliseconds(160), 0,
                new StartNoteCommand((ObjectId)12U),
                new ApplySampleOffsetCommand(3))), 2, root);
        timeline.Add(new RawSource(
            At(0.5, 0, new NoteOffCommand())), 1,
            root.FlattenedChild(physicalChannelOffset: 2));

        List<NoteEvent> emitted = [];
        bool cancelled = false;
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
        {
            if (step is not IncrementalPatternTimelineStep.Emit e)
                continue;
            emitted.Add(e.Note);
            if (e.Note.Commands.Any(c => c is NoteOffCommand))
            {
                cancelled = timeline.Cancel(pending);
                break;
            }
        }
        emitted.AddRange(Drain(timeline));
        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.True);
            Assert.That(emitted.SelectMany(e => e.Commands)
                .OfType<StartNoteCommand>(), Is.Empty);
            Assert.That(root.GetPhysicalChannelState(0)
                .TryGetEffectParameter(EffectMemorySlot.SampleOffset, out _),
                Is.False);
        });
    }

    private static NoteEvent At(double row, int channel, params NoteCommand[] commands)
        => At(row, TimeSpan.Zero, ChannelTarget.Physical(channel), commands);

    private static NoteEvent At(double row, ChannelTarget target,
        params NoteCommand[] commands)
        => At(row, TimeSpan.Zero, target, commands);

    private static NoteEvent At(double row, TimeSpan fixedWall, int channel,
        params NoteCommand[] commands)
        => At(row, fixedWall, ChannelTarget.Physical(channel), commands);

    private static NoteEvent At(double row, TimeSpan fixedWall,
        ChannelTarget target, params NoteCommand[] commands)
        => new(new MusicalTime(fixedWall, row), target, commands);

    private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
    {
        List<NoteEvent> notes = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit e)
                notes.Add(e.Note);
        return notes.ToArray();
    }

    private sealed class RawSource(params NoteEvent[] events)
        : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            foreach (NoteEvent item in events)
                yield return new RawPatternStep.Emit(item);
        }
    }
}
