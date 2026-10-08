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
public sealed class IncrementalTrackerTickEffectsTests
{
    [TestCase((byte)0, 20)]
    [TestCase((byte)1, 20)]
    [TestCase((byte)3, 60)]
    public void SCxCutsAtFirstPostStartOrRequestedTick(byte tick, int milliseconds)
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteCutPatternEffect(tick));
        AssertParity(p);
        NoteEvent[] events = Execute(p, out _);
        Assert.That(events[1].Offset.TimeOffset,
            Is.EqualTo(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.That(events[1].Commands.Single(), Is.TypeOf<NoteCutCommand>());
    }

    [TestCase((byte)0, 20)]
    [TestCase((byte)1, 20)]
    [TestCase((byte)3, 60)]
    public void SDxDelaysStartAndSetupAtomically(byte tick, int milliseconds)
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteDelayPatternEffect(tick));
        AssertParity(p);
        NoteEvent[] events = Execute(p, out _);
        Assert.That(events, Has.Length.EqualTo(1));
        Assert.That(events[0].Offset.TimeOffset,
            Is.EqualTo(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.That(events[0].Commands[0], Is.TypeOf<StartNoteCommand>());
    }

    [Test]
    public void OutOfSpanSCxAndSDxAreSuppressedWithoutRunningCommands()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteCutPatternEffect(6));
        c.Effects.Add(new TrackerNoteDelayPatternEffect(6));
        AssertParity(p);
        Assert.That(Execute(p, out _), Is.Empty);
    }

    [Test]
    public void SCxAndSDxShareEffectiveS6xSpan()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
        c.Effects.Add(new TrackerNoteCutPatternEffect(8));
        c.Effects.Add(new TrackerNoteDelayPatternEffect(3));
        AssertParity(p);
        NoteEvent[] events = Execute(p, out TimeSpan duration);
        Assert.That(events.Select(e => e.Offset.TimeOffset),
            Is.EqualTo(new[] { TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(160) }));
        Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
    }

    [Test]
    public void SCxDoesNotRepeatAcrossSEyButSDxRepeatsAtomicStart()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteCutPatternEffect(3));
        c.Effects.Add(new TrackerNoteDelayPatternEffect(3));
        c.Effects.Add(new TrackerPatternDelayPatternEffect(2));
        AssertParity(p);
        NoteEvent[] notes = Execute(p, out TimeSpan duration);
        Assert.That(notes.Select(e => e.Offset.TimeOffset),
            Is.EqualTo(new[] {
                TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(300) }));
        Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
    }

    [Test]
    public void SDxAtomicSetupWithSampleOffsetAndVolumeMatchesEager()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new SetNoteVolumePatternEffect(0.5));
        c.Effects.Add(new SampleOffsetPatternEffect(1));
        c.Effects.Add(new TrackerNoteDelayPatternEffect(3));
        AssertParity(p);
    }

    [Test]
    public void Q03RetriggerOccursAtThirdTickAndCarriesCountdownToQ00()
    {
        DataPatternDefinition p = Pattern(rows: 2);
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new RetriggerPatternEffect(0x03));
        p.Grid.GetOrCreateCell(1, 0).Effects.Add(new RetriggerPatternEffect(0));
        AssertParity(p);
        NoteEvent[] notes = Execute(p, out _);
        Assert.That(notes.Select(x => x.Offset.TimeOffset),
            Is.EqualTo(new[] {
                TimeSpan.Zero, TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(180) }));
    }

    [Test]
    public void Q90RetriggersEveryPostStartTickWithVolumeTransformNine()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new RetriggerPatternEffect(0x90));
        AssertParity(p);
        NoteEvent[] notes = Execute(p, out _);
        Assert.That(notes, Has.Length.EqualTo(6));
        Assert.That(notes.Skip(1).All(e =>
            e.Commands.Single() == new RetriggerCurrentVoiceCommand(9)), Is.True);
    }

    [Test]
    public void QxxRespectsExtendedS6xAndRepeatedSEyTicks()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new RetriggerPatternEffect(0x03));
        c.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));
        c.Effects.Add(new TrackerPatternDelayPatternEffect(1));
        AssertParity(p);
    }

    [Test]
    public void ChildTempoSetRetimesPendingSCxAndQxxTickDeadlines()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteCutPatternEffect(4));
        c.Effects.Add(new RetriggerPatternEffect(0x03));
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(p, 1, root);
        timeline.Add(new RawSource(new NoteEvent(new MusicalTime(TimeSpan.Zero, 0),
            ChannelTarget.Global, [new SetTempoCommand(250)])),
            1, root.FlattenedChild(physicalChannelOffset: 2));
        List<NoteEvent> result = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emit)
                result.Add(emit.Note);
        Assert.That(result.Single(e => e.Commands.Any(c => c is NoteCutCommand))
            .Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(40)));
        Assert.That(result.Single(e => e.Commands.Any(c => c is RetriggerCurrentVoiceCommand))
            .Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(30)));
    }

    [Test]
    public void CancellingInvocationDiscardsPendingSCxAndRetriggerTicks()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteCutPatternEffect(4));
        c.Effects.Add(new RetriggerPatternEffect(0x03));
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        long id = timeline.Add(p, 1, root);
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
        {
            if (step is IncrementalPatternTimelineStep.Emit e
                && e.Note.Commands.Any(c => c is StartNoteCommand))
                break;
        }
        Assert.That(timeline.Cancel(id), Is.True);
        Assert.That(timeline.TryStep(out _), Is.False);
    }

    [Test]
    public void RepeatedDelayedChildNoteUsesMappedChannelExactlyOnce()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteDelayPatternEffect(2));
        c.Effects.Add(new TrackerPatternDelayPatternEffect(1));
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(p, 1, root.FlattenedChild(physicalChannelOffset: 5));
        List<NoteEvent> actual = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emit)
                actual.Add(emit.Note);

        Assert.That(actual.Select(e => e.Offset.TimeOffset),
            Is.EqualTo(new[] { TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(160) }));
        Assert.That(actual.Select(e => e.Target),
            Is.All.EqualTo(ChannelTarget.Physical(5)));
    }

    [Test]
    public void Q00UsesExistingCountdownOnMappedChildAcrossPatternInvocations()
    {
        SequencingContext root = new();
        DataPatternDefinition first = Pattern();
        PatternCell firstCell = first.Grid.GetOrCreateCell(0, 0);
        firstCell.Note = new StartPatternNote((ObjectId)10U);
        firstCell.Effects.Add(new RetriggerPatternEffect(0xA3));
        DataPatternDefinition next = Pattern();
        next.Grid.GetOrCreateCell(0, 0).Effects.Add(new RetriggerPatternEffect(0x00));

        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(first, 1, root.FlattenedChild(physicalChannelOffset: 3));
        List<NoteEvent> actual = [];
        bool addedNext = false;
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
        {
            if (step is IncrementalPatternTimelineStep.Emit emit)
                actual.Add(emit.Note);
            if (!addedNext && timeline.Elapsed == TimeSpan.FromMilliseconds(120))
            {
                addedNext = true;
                timeline.Add(next, 1, root.FlattenedChild(physicalChannelOffset: 3));
            }
        }

        NoteEvent[] retriggers = actual.Where(e =>
            e.Commands.Any(c => c is RetriggerCurrentVoiceCommand)).ToArray();
        Assert.That(retriggers.Select(x => x.Offset.TimeOffset),
            Is.EqualTo(new[] {
                TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120),
                TimeSpan.FromMilliseconds(180) }));
        Assert.That(retriggers.Select(e => e.Target),
            Is.All.EqualTo(ChannelTarget.Physical(3)));
        Assert.That(retriggers.All(e => e.Commands.Single()
            == new RetriggerCurrentVoiceCommand(0x0A)), Is.True);
    }

    [Test]
    public void SDxWithQxxIsExplicitlyUnsupportedWithoutMutatingRetriggerMemory()
    {
        DataPatternDefinition p = Pattern();
        PatternCell c = p.Grid.GetOrCreateCell(0, 0);
        c.Note = new StartPatternNote((ObjectId)10U);
        c.Effects.Add(new TrackerNoteDelayPatternEffect(2));
        c.Effects.Add(new RetriggerPatternEffect(0x03));
        SequencingContext root = new();
        using IncrementalPatternTimeline timeline = new(root);
        timeline.Add(p, 1, root);
        Assert.Throws<NotSupportedException>(() => timeline.TryStep(out _));
        Assert.That(root.GetPhysicalChannelState(0).TryGetEffectParameter(
            EffectMemorySlot.Retrigger, out _), Is.False);
    }

    private static DataPatternDefinition Pattern(int rows = 1)
        => new((ObjectId)1U, "Ticks") { RowCount = rows, ChannelCount = 1 };

    private static NoteEvent[] Execute(DataPatternDefinition p, out TimeSpan duration)
    {
        SequencingContext state = new();
        using IncrementalPatternTimeline timeline = new(state);
        timeline.Add(p, p.RowCount, state);
        List<NoteEvent> result = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emit)
                result.Add(emit.Note);
        duration = timeline.Elapsed;
        return result.ToArray();
    }

    private static void AssertParity(DataPatternDefinition p)
    {
        SequencingContext eager = new();
        NoteScheduleBuilder builder = new();
        PatternNoteProcessor.GenerateNotes(p, eager, builder, out TimeSpan expectedDuration);
        NoteEvent[] expected = builder.Freeze().ToArray();
        NoteEvent[] actual = Execute(p, out TimeSpan actualDuration);
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
                Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds).Within(1e-6));
            Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target));
            Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands));
        }
        Assert.That(actualDuration.TotalSeconds,
            Is.EqualTo(expectedDuration.TotalSeconds).Within(1e-6));
    }

    private sealed class RawSource(params NoteEvent[] notes) : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            foreach (NoteEvent note in notes)
                yield return new RawPatternStep.Emit(note);
        }
    }
}
