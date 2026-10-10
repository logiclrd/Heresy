using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackDelayedLifecycleTests
{
    [Test]
    public void DeferredS7xAndVirtualPastControlsActOnVoicesAtTheirRealDeadline()
    {
        ObjectId physicalId = (ObjectId)10U;
        ObjectId virtualId = (ObjectId)11U;
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(new RawSource(
            At(0, ChannelTarget.Physical(0), new StartNoteCommand(physicalId)),
            At(0, ChannelTarget.Virtual(7), new StartNoteCommand(virtualId)),
            At(0, TimeSpan.FromMilliseconds(35), ChannelTarget.Physical(0),
                new ApplyTrackerNewNoteActionCommand(NoteDisplacementAction.Continue)),
            At(0, TimeSpan.FromMilliseconds(45), ChannelTarget.Virtual(7),
                new SetCurrentVoiceDisplacementActionCommand(
                    NoteDisplacementAction.Continue)),
            At(0.5, ChannelTarget.Physical(0), new StartNoteCommand(physicalId)),
            At(0.5, ChannelTarget.Virtual(7), new StartNoteCommand(virtualId)),
            At(0.5, TimeSpan.FromMilliseconds(20), ChannelTarget.Physical(0),
                new ApplyTrackerPastNoteActionCommand(TrackerPastNoteAction.Cut)),
            At(0.5, TimeSpan.FromMilliseconds(30), ChannelTarget.Virtual(7),
                new ApplyPastNoteActionCommand(TrackerPastNoteAction.Cut))),
            2, context);

        NoteScheduleBuilder builder = new();
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emit)
                builder.Append(emit.Note);

        SampleDefinition definition = new(
            (ObjectId)1U, "Sample", new ExternalAssetReference("constant.raw"));
        float[] waveform = new float[300];
        Array.Fill(waveform, 1.0f);
        SampleSound sound = new(
            definition,
            new MemorySampleData(1000, 1, waveform));
        PlaybackSession session = new(
            new RenderContext(new RenderConfiguration(1000,
                [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            builder.Freeze(),
            new Resolver(physicalId, virtualId, sound));

        float[] output = new float[120];
        session.Render(0, 75, output.AsSpan(0, 75));
        Assert.That(output[70], Is.EqualTo(4f).Within(1e-6f),
            "Both displaced voices must survive because the deferred NNA " +
            "overrides ran before the second note starts.");
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(2));

        session.Render(75, 10, output.AsSpan(75, 10));
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1),
            "The deferred physical S70 cuts only its migrated physical note.");

        session.Render(85, 35, output.AsSpan(85, 35));
        Assert.That(session.VirtualVoices, Is.Empty,
            "The deferred virtual past-note action cuts its own old note.");
        Assert.That(output[115], Is.EqualTo(2f).Within(1e-6f),
            "Two current notes remain audible after both past-note cuts.");
    }

    private static NoteEvent At(
        double row, ChannelTarget target, params NoteCommand[] commands)
        => At(row, TimeSpan.Zero, target, commands);

    private static NoteEvent At(
        double row, TimeSpan wall, ChannelTarget target,
        params NoteCommand[] commands)
        => new(new MusicalTime(wall, row), target, commands);

    private sealed class RawSource(params NoteEvent[] events)
        : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(
            SequencingContext context)
        {
            foreach (NoteEvent note in events)
                yield return new RawPatternStep.Emit(note);
        }
    }

    private sealed class Resolver(
        ObjectId physicalId, ObjectId virtualId, ISound sound)
        : ISoundResolver
    {
        public bool TryResolve(
            ObjectId sourceId, bool mixdown, out ISound? resolved)
        {
            resolved = !mixdown
                && (sourceId == physicalId || sourceId == virtualId)
                    ? sound : null;
            return resolved is not null;
        }
    }
}
