using System;
using System.Collections.Generic;
using System.Linq;
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
public sealed class PlaybackTickWallLifecycleCollisionTests
{
    [Test]
    public void QxyTickAndWallS74TieWithSdxStartScxCutAndLateS70()
    {
        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        ObjectId sampleId = (ObjectId)10U;
        timeline.Add(new RawSource(
            At(0, TimeSpan.Zero,
                new StartNoteCommand(sampleId),
                new ApplyRetriggerCommand(0x02)),
            // This wall action coincides with Q02's first actual
            // retrigger at 40 ms. It affects the current note's NNA,
            // not its pitch or the pending SDx note.
            At(0, TimeSpan.FromMilliseconds(40),
                new ApplyTrackerNewNoteActionCommand(
                    NoteDisplacementAction.Continue)),
            At(0, TimeSpan.Zero, new ApplyTrackerNoteCutCommand(5)),
            // Ordinary fixed-wall S70 is permitted to outlive the
            // original 120-ms tracker row; it targets the NNA pool.
            At(0, TimeSpan.FromMilliseconds(130),
                new ApplyTrackerPastNoteActionCommand(
                    TrackerPastNoteAction.Cut)),
            // Nominal event at 30 ms + two ticks (40 ms) + 5 ms.
            At(0.25, TimeSpan.FromMilliseconds(5),
                new StartNoteCommand(sampleId),
                new ApplyTrackerNoteDelayCommand(2))), 1, context);

        NoteScheduleBuilder builder = new();
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emit)
                builder.Append(emit.Note);
        NoteSchedule schedule = builder.Freeze();
        Assert.Multiple(() =>
        {
            Assert.That(schedule.Any(e => e.Offset.TimeOffset
                == TimeSpan.FromMilliseconds(40)
                && e.Commands.Any(c => c is
                    SetCurrentVoiceDisplacementActionCommand)), Is.True);
            Assert.That(schedule.Any(e => e.Offset.TimeOffset
                == TimeSpan.FromMilliseconds(75)
                && e.Commands.Any(c => c is StartNoteCommand)), Is.True);
            Assert.That(schedule.Any(e => e.Offset.TimeOffset
                == TimeSpan.FromMilliseconds(100)
                && e.Commands.Any(c => c is NoteCutCommand)), Is.True);
            Assert.That(schedule.Any(e => e.Offset.TimeOffset
                == TimeSpan.FromMilliseconds(130)
                && e.Commands.Any(c => c is ApplyPastNoteActionCommand)), Is.True);
        });

        float[] largeBlock = Render(schedule, 160);
        float[] smallBlocks = Render(schedule, 160, 1, 7, 13);
        Assert.That(smallBlocks, Is.EqualTo(largeBlock).Within(1e-6f),
            "Qxy/SDx/SCx/S7x boundaries must be PCM-block invariant.");
        Assert.Multiple(() =>
        {
            Assert.That(largeBlock[35], Is.EqualTo(1f).Within(1e-6f),
                "The initial note plays before the delayed S74.");
            Assert.That(largeBlock[70], Is.EqualTo(1f).Within(1e-6f),
                "SDx has not emitted its delayed new start.");
            Assert.That(largeBlock[76], Is.EqualTo(2f).Within(1e-6f),
                "The old note continues under the new note due to S74.");
            Assert.That(largeBlock[95], Is.EqualTo(2f).Within(1e-6f));
            Assert.That(largeBlock[125], Is.EqualTo(1f).Within(1e-6f),
                "The SCx cut stops the current note, not the NNA tail.");
            Assert.That(largeBlock[150], Is.Zero.Within(1e-6f),
                "The wall-delayed S70 removes the surviving NNA tail.");
        });
    }

    private static float[] Render(NoteSchedule schedule, int count,
        params int[] chunkSizes)
    {
        ObjectId id = (ObjectId)10U;
        SampleSound sample = new(
            new SampleDefinition(id, "PCM", new ExternalAssetReference("pcm.raw")),
            new MemorySampleData(1000, 1, Enumerable.Repeat(1f, 300).ToArray()));
        PlaybackSession session = new(
            new RenderContext(new RenderConfiguration(1000,
                [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            schedule, new Resolver(id, sample));
        float[] output = new float[count];
        int position = 0;
        int block = 0;
        while (position < count)
        {
            int length = chunkSizes.Length == 0 ? count - position
                : Math.Min(chunkSizes[block++ % chunkSizes.Length],
                    count - position);
            session.Render(position, length, output.AsSpan(position, length));
            position += length;
        }
        return output;
    }

    private static NoteEvent At(double row, TimeSpan wall,
        params NoteCommand[] commands)
        => new(new MusicalTime(wall, row), ChannelTarget.Physical(0), commands);

    private sealed class RawSource(params NoteEvent[] notes)
        : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            foreach (NoteEvent note in notes)
                yield return new RawPatternStep.Emit(note);
        }
    }

    private sealed class Resolver(ObjectId id, ISound sample)
        : ISoundResolver
    {
        public bool TryResolve(ObjectId requested, bool mixdown,
            out ISound? result)
        {
            result = !mixdown && requested == id ? sample : null;
            return result is not null;
        }
    }
}
