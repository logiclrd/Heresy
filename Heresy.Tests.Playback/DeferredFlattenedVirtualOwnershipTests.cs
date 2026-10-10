using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Two flattened roots start on hosts 0 and 1. Their child channels
/// 1 and 0 both map to physical channel 1, but have different playback
/// owners. Their virtual channel 7 is similarly scoped by child cursor.
/// Delayed commands, displaced NNA voices and broadcasts use the actual
/// production recursive scheduler and PCM source.
/// </summary>
[TestFixture]
public sealed class DeferredFlattenedVirtualOwnershipTests
{
    private static readonly ObjectId SampleId = (ObjectId)90U;

    [Test]
    public void ScopedBroadcastAndDeferredPhysicalPastCutKeepSiblingOwnerAlive()
    {
        TestPattern firstChild = Pattern(3, 2,
            At(0, TimeSpan.FromMilliseconds(50),
                ChannelTarget.Physical(1),
                new ApplyTrackerPastNoteActionCommand(
                    TrackerPastNoteAction.Cut)),
            At(0, ChannelTarget.Physical(1),
                new StartNoteCommand(SampleId)),
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)),
            At(0.1, ChannelTarget.Virtual(7),
                new SetCurrentVoiceDisplacementActionCommand(
                    NoteDisplacementAction.Continue),
                new StartNoteCommand(SampleId)),
            At(0.2, ChannelTarget.AllVirtualInScope,
                new NoteCutCommand()),
            At(0.3, ChannelTarget.Physical(1),
                new ApplyTrackerNewNoteActionCommand(
                    NoteDisplacementAction.Continue),
                new StartNoteCommand(SampleId)));
        TestPattern secondChild = Pattern(4, 1,
            At(0, ChannelTarget.Physical(0),
                new StartNoteCommand(SampleId)),
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)),
            At(0, TimeSpan.FromMilliseconds(60),
                ChannelTarget.AllVirtual, new NoteCutCommand()));
        using Scenario scenario = Create(firstChild, secondChild);
        using PreparedIncrementalAudioSource pcm =
            new(scenario.Timeline, Session());

        float[] output = Render(pcm, 130, [1, 7, 13, 2, 19, 5, 41]);
        Assert.Multiple(() =>
        {
            Assert.That(output[5], Is.EqualTo(5f).Within(1e-6f),
                "Both physical child voices, same-numbered virtual notes, " +
                "and the unrelated root host must sound.");
            Assert.That(output[15], Is.EqualTo(6f).Within(1e-6f),
                "The first child's displaced virtual NNA voice survives.");
            Assert.That(output[30], Is.EqualTo(5f).Within(1e-6f),
                "Scoped virtual broadcast cuts only the first child's " +
                "current virtual note, never its NNA or sibling's virtual 7.");
            Assert.That(output[44], Is.EqualTo(6f).Within(1e-6f),
                "The first child's displaced physical voice still sounds.");
            Assert.That(output[55], Is.EqualTo(5f).Within(1e-6f),
                "Deferred S70 cuts the first child's old physical NNA " +
                "voice, not the sibling's physical voice on the same host.");
            Assert.That(output[85], Is.EqualTo(3f).Within(1e-6f),
                "Global virtual broadcast reaches the remaining virtual " +
                "NNA and sibling channel, but not physical voices.");
            Assert.That(output[129], Is.EqualTo(3f).Within(1e-6f));
            Assert.That(pcm.Session.VirtualVoices, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CancelingFlattenedRootDropsItsPendingPhysicalVirtualAndS7xActions(
        bool chunked)
    {
        TestPattern firstChild = Pattern(3, 2,
            At(0, ChannelTarget.Physical(1),
                new StartNoteCommand(SampleId)),
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)),
            At(0, TimeSpan.FromMilliseconds(90),
                ChannelTarget.Physical(1), new NoteCutCommand()),
            At(0, TimeSpan.FromMilliseconds(100),
                ChannelTarget.Virtual(7), new NoteOffCommand()),
            At(0, TimeSpan.FromMilliseconds(110),
                ChannelTarget.Virtual(7),
                new ApplyPastNoteActionCommand(TrackerPastNoteAction.Cut)),
            At(0, TimeSpan.FromMilliseconds(115),
                ChannelTarget.Physical(1),
                new ApplyTrackerNewNoteActionCommand(
                    NoteDisplacementAction.Fade)),
            At(0.1, ChannelTarget.Virtual(7),
                new SetCurrentVoiceDisplacementActionCommand(
                    NoteDisplacementAction.Continue),
                new StartNoteCommand(SampleId)));
        TestPattern secondChild = Pattern(4, 1,
            At(0, ChannelTarget.Physical(0),
                new StartNoteCommand(SampleId)),
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)),
            At(0, TimeSpan.FromMilliseconds(130),
                ChannelTarget.Physical(0), new NoteOffCommand()));
        using Scenario scenario = Create(firstChild, secondChild);
        using PreparedIncrementalAudioSource pcm =
            new(scenario.Timeline, Session());

        float[] first = Render(pcm, 30, chunked ? [1, 3, 11, 2] : [30]);
        Assert.That(first[20], Is.EqualTo(6f).Within(1e-6f));
        Assert.That(pcm.Session.VirtualVoices, Has.Count.EqualTo(1),
            "The first child's displaced virtual voice must be live before cancellation.");
        Assert.That(pcm.Cancel(scenario.FirstRoot), Is.True);
        Assert.That(pcm.Cancel(scenario.FirstRoot), Is.False);
        Assert.That(scenario.Timeline.IsInvocationActive(scenario.SecondRoot), Is.True);
        Assert.That(pcm.Session.VirtualVoices, Is.Empty,
            "Cancellation must cut the NNA voice owned by the canceled " +
            "child but never the sibling's same-numbered virtual channel.");

        float[] rest = Render(pcm, 150,
            chunked ? [1, 7, 13, 2, 19, 3, 11] : [150]);
        Assert.Multiple(() =>
        {
            Assert.That(rest[40], Is.EqualTo(3f).Within(1e-6f),
                "The surviving mapped physical voice, virtual 7 and root " +
                "host remain after canceled cut tails have decayed.");
            Assert.That(rest[75], Is.EqualTo(3f).Within(1e-6f),
                "Canceled +90-ms physical Cut and +100/+110-ms virtual " +
                "deadlines cannot change a sibling note.");
            Assert.That(rest[120], Is.EqualTo(2f).Within(1e-6f),
                "The sibling's own +130-ms Note Off still executes.");
            Assert.That(scenario.Timeline.IsInvocationActive(scenario.FirstRoot),
                Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReleasedVirtualVoiceOutlivesProducerUnlessItsOwnerIsExplicitlyCanceled(
        bool cancelProducer)
    {
        TestPattern releasingChild = Pattern(3, 2,
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)),
            At(0, TimeSpan.FromMilliseconds(20), ChannelTarget.Virtual(7),
                new NoteOffCommand()));
        TestPattern sibling = Pattern(4, 1,
            At(0, ChannelTarget.Physical(0),
                new StartNoteCommand(SampleId)),
            At(0, ChannelTarget.Virtual(7),
                new StartNoteCommand(SampleId)));
        using Scenario scenario = Create(releasingChild, sibling);
        using PreparedIncrementalAudioSource pcm =
            new(scenario.Timeline, Session(new ReleasingSound(300)));

        float[] initial = Render(pcm, 30, [1, 7, 13]);
        Assert.That(initial[25], Is.EqualTo(4f).Within(1e-6f),
            "Note Off must not immediately cut a finite release.");

        if (cancelProducer)
        {
            Assert.That(pcm.Cancel(scenario.FirstRoot), Is.True);
            Assert.That(pcm.Session.VirtualVoices, Is.Empty);
        }

        // Each child has two 120-ms rows. Rendering through frame 260
        // crosses natural producer completion; it must not force-cut an
        // existing virtual voice that is still releasing until frame 320.
        float[] middle = Render(pcm, 240, [1, 3, 89, 7]);
        Assert.Multiple(() =>
        {
            Assert.That(middle[60],
                Is.EqualTo(cancelProducer ? 3f : 4f).Within(1e-6f),
                "Only explicit subtree cancellation may cut this tail.");
            Assert.That(middle[230],
                Is.EqualTo(cancelProducer ? 3f : 4f).Within(1e-6f),
                "Natural cursor retirement must preserve an already-" +
                "releasing scoped virtual note and its sibling.");
            Assert.That(scenario.Timeline.IsInvocationActive(
                scenario.FirstRoot), Is.False);
        });

        float[] afterRelease = Render(pcm, 75, [5, 1, 11]);
        Assert.That(afterRelease[65], Is.EqualTo(3f).Within(1e-6f),
            "The releasing voice expires at its own source deadline; " +
            "the sibling's mapped physical, virtual and host notes survive.");
    }

    private static Scenario Create(TestPattern firstChild, TestPattern secondChild)
    {
        TestPattern first = Pattern(1, 1,
            At(0, ChannelTarget.Physical(0),
                new StartNoteCommand(firstChild.Id)));
        TestPattern second = Pattern(2, 3,
            At(0, ChannelTarget.Physical(1),
                new StartNoteCommand(secondChild.Id)),
            At(0, ChannelTarget.Physical(2),
                new StartNoteCommand(SampleId)));
        IncrementalRecursiveTimeline timeline = new(
            new SequencingContext(),
            new Resolver(first, second, firstChild, secondChild));
        long firstRoot = timeline.AddRoot(first.Id);
        long secondRoot = timeline.AddRoot(second.Id);
        return new Scenario(timeline, firstRoot, secondRoot);
    }

    private static TestPattern Pattern(uint id, int channels,
        params NoteEvent[] events) => new((ObjectId)id, channels, events);

    private static NoteEvent At(double row, ChannelTarget target,
        params NoteCommand[] commands)
        => At(row, TimeSpan.Zero, target, commands);

    private static NoteEvent At(double row, TimeSpan wall, ChannelTarget target,
        params NoteCommand[] commands)
        => new(new MusicalTime(wall, row), target, commands);

    private static float[] Render(PreparedIncrementalAudioSource pcm,
        int frames, int[] sizes)
    {
        float[] buffer = new float[frames];
        int written = 0;
        int step = 0;
        while (written < frames)
        {
            int size = Math.Min(frames - written, sizes[step++ % sizes.Length]);
            pcm.Render(size, buffer.AsSpan(written, size));
            written += size;
        }
        return buffer;
    }

    private static PlaybackSession Session(ISound? overrideSound = null)
    {
        SampleDefinition definition = new(
            SampleId, "Sample", new ExternalAssetReference("constant.raw"));
        SampleSound sound = new(
            definition,
            new MemorySampleData(1000, 1,
                Enumerable.Repeat(1f, 400).ToArray()),
            NewNotePolicy.Continue);
        return new PlaybackSession(
            new RenderContext(new RenderConfiguration(1000,
                [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            new NoteScheduleBuilder().Freeze(),
            new SoundResolver(overrideSound ?? sound));
    }

    private sealed class Scenario(
        IncrementalRecursiveTimeline timeline,
        long firstRoot, long secondRoot) : IDisposable
    {
        public IncrementalRecursiveTimeline Timeline { get; } = timeline;
        public long FirstRoot { get; } = firstRoot;
        public long SecondRoot { get; } = secondRoot;
        public void Dispose() => Timeline.Dispose();
    }

    private sealed class TestPattern : PatternDefinition,
        IIncrementalRawPatternNoteGenerator
    {
        private readonly NoteEvent[] _events;

        public TestPattern(ObjectId id, int channels, NoteEvent[] events)
            : base(id, "Test")
        {
            RowCount = 2;
            ChannelCount = channels;
            _events = events;
        }

        public IEnumerable<RawPatternStep> EnumerateRawSteps(
            SequencingContext context)
        {
            foreach (NoteEvent e in _events)
                yield return new RawPatternStep.Emit(e);
        }
    }

    private sealed class Resolver(params SongObject[] objects)
        : IIncrementalInvocationResolver
    {
        private readonly Dictionary<ObjectId, SongObject> _objects =
            objects.ToDictionary(x => x.Id);

        public bool TryResolve(ObjectId id, out SongObject? value)
            => _objects.TryGetValue(id, out value);
    }

    private sealed class ReleasingSound(int releaseFrames) : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => NoteConfigurationSnapshot.Default;

        public SoundState CreateState() => new ReleasingState();

        public long? GetEndFrameExclusive(
            RenderContext context, SoundState state)
            => state.NoteOffTime is TimeSpan off
                ? checked(FrameTime.Ceiling(off, context.Configuration.SampleRate)
                    + releaseFrames)
                : null;

        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            long? end = GetEndFrameExclusive(context, state);
            for (int i = 0; i < frameCount; i++)
                if (!end.HasValue || startFrame + i < end.Value)
                    destination[i] += 1f;
        }
    }

    private sealed class ReleasingState : SoundState { }

    private sealed class SoundResolver(ISound sound) : ISoundResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? resolved)
        {
            resolved = !mixdown && id == SampleId ? sound : null;
            return resolved is not null;
        }
    }
}
