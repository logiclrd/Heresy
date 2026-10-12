using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class ChannelOutputFilterRenderingTests
{
    private const int Rate = 1000;
    private static readonly ObjectId PulseId = (ObjectId)880U;

    [Test]
    public void SamePhysicalHostUsesOneOutputFilterAcrossDifferentFlattenedOwners()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(881, 0.001));
        // Two simultaneous logical source owners share the same physical host.
        session.ApplyScopedEvent(12, ChannelTarget.Physical(0),
            [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 12);
        session.ApplyScopedEvent(13, ChannelTarget.Physical(0),
            [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 13);
        // Identical host index but a separate real host: should not be filtered.
        session.ApplyScopedEvent(13, ChannelTarget.Physical(1),
            [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 13);

        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 3, 2, 0, 0, 0 }));
        Assert.That(session.GetChannelState(0, 12).CurrentVoice, Is.Null);
        Assert.That(session.GetChannelState(0, 13).CurrentVoice, Is.Null);
    }

    [Test]
    public void ChannelHistorySurvivesNoteReplacementNnaAndSilence()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound(continueOnDisplacement: true));
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId)),
            Event(1, 0, new StartNoteCommand(PulseId)),
            Event(5, 0, new StartNoteCommand(PulseId))));
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(891, 0.002));
        float[] pcm = new float[9];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[]
        {
            1, 1, 1, 1, 0, 1, 0, 1, 0,
        }), "channel histories persist between notes and process displaced " +
            "NNA voices on the original output host");
    }

    [Test]
    public void ChannelFilterRunsAfterNoteEventFilterAndBeforeMasterSpeakerSum()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId)
            {
                EventFilterConfiguration = Echo(901, 0.001),
            })));
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(902, 0.002));
        float[] pcm = new float[6];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 1, 1, 0, 0 }));
    }

    [Test]
    public void ChannelFilterTailKeepsSessionNonquiescentAfterSourceEnds()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId))));
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(911, 0.002));
        float[] first = new float[1];
        session.Render(0, 1, first);
        session.EndInput();
        Assert.That(first, Is.EqualTo(new float[] { 1 }));
        Assert.That(session.IsQuiescent, Is.False,
            "the host Filter owns a tail after the source and note retire");

        float[] rest = new float[2];
        session.Render(1, 2, rest);
        Assert.That(rest, Is.EqualTo(new float[] { 0, 1 }));
        Assert.That(session.IsQuiescent, Is.True);
    }

    [Test]
    public void ScopedAndUnscopedVirtualOutputHostsDoNotShareHistories()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ConfigureVirtualChannelOutputFilter(7, Echo(921, 0.001),
            scopedOwner: 12);
        session.ConfigureVirtualChannelOutputFilter(7, Echo(922, 0.002));
        session.ApplyScopedEvent(12, ChannelTarget.Virtual(7),
            [new StartNoteCommand(PulseId)]);
        session.ApplyScopedEvent(13, ChannelTarget.Virtual(7),
            [new StartNoteCommand(PulseId)]);
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(PulseId)]);
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 3, 1, 1, 0, 0 }));
    }

    [Test]
    public void WholeAndChunkedBlocksPreserveMultiOwnerHostOutputAndFiniteTail()
    {
        PlaybackSession Create()
        {
            TestResolver sounds = new();
            sounds.Add(PulseId, new PulseSound(continueOnDisplacement: true));
            PlaybackSession session = Session(sounds, Schedule(
                Event(0, 0, new StartNoteCommand(PulseId)),
                Event(3, 0, new StartNoteCommand(PulseId))));
            session.ConfigurePhysicalChannelOutputFilter(0, Echo(931, 0.002));
            session.ConfigurePhysicalChannelOutputFilter(1, Echo(932, 0.001));
            session.ApplyScopedEvent(42, ChannelTarget.Physical(0),
                [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 42);
            session.ApplyScopedEvent(42, ChannelTarget.Physical(1),
                [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 42);
            return session;
        }

        float[] whole = new float[11], chunked = new float[11];
        Create().Render(0, whole.Length, whole);
        PlaybackSession partitioned = Create();
        partitioned.Render(0, 1, chunked.AsSpan(0, 1));
        partitioned.Render(1, 2, chunked.AsSpan(1, 2));
        partitioned.Render(3, 3, chunked.AsSpan(3, 3));
        partitioned.Render(6, 5, chunked.AsSpan(6, 5));
        Assert.That(chunked, Is.EqualTo(whole));
    }

    [Test]
    public void ClearingConfiguredHostDoesNotAlterUnrelatedHostAndBypassesFutureNotes()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId)),
            Event(0, 1, new StartNoteCommand(PulseId)),
            Event(2, 0, new StartNoteCommand(PulseId)),
            Event(2, 1, new StartNoteCommand(PulseId))));
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(941, 0.001));
        session.ConfigurePhysicalChannelOutputFilter(1, Echo(942, 0.002));
        float[] first = new float[2];
        session.Render(0, 2, first);
        Assert.That(first, Is.EqualTo(new float[] { 2, 1 }));
        session.ConfigurePhysicalChannelOutputFilter(0, null);
        float[] second = new float[4];
        session.Render(2, 4, second);
        Assert.That(second, Is.EqualTo(new float[] { 3, 0, 1, 0 }),
            "other host retains its prior two-frame delay while host zero bypasses");
    }

    private static FilterInstance Echo(uint id, double seconds)
    {
        FilterDefinition definition = new((ObjectId)id, "Echo", [
            new FilterStageDefinition(1, "delay", FilterKind.CombFeedforward),
        ]);
        FilterInstance result = new(definition);
        result.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(seconds)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return result;
    }

    private static PlaybackSession Session(
        TestResolver resolver, NoteSchedule? schedule = null)
        => new(new RenderContext(new RenderConfiguration(
                Rate, [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            schedule ?? Schedule(), resolver);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder builder = new();
        foreach (NoteEvent note in events) builder.Append(note);
        return builder.Freeze();
    }

    private static NoteEvent Event(int ms, int channel, params NoteCommand[] cmds)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(ms), 0),
            ChannelTarget.Physical(channel), cmds);

    private sealed class ProbeState : SoundState { }

    private sealed class PulseSound : ISound
    {
        private readonly bool _continue;
        public PulseSound(bool continueOnDisplacement = false)
            => _continue = continueOnDisplacement;
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(_continue ? NewNotePolicy.Continue : NewNotePolicy.Cut);
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state) => 1;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            if (startFrame == 0 && frameCount > 0) destination[0] += 1;
        }
    }

    private sealed class TestResolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Add(ObjectId id, ISound sound) => _sounds[id] = sound;
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
            => _sounds.TryGetValue(id, out sound);
    }
}
