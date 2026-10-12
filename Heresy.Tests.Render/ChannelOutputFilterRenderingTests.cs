using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
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

    [TestCase(2)]
    [TestCase(8)]
    public void NnaForkPreservesItsOwnResponseAfterOriginalPhysicalHostIsCleared(
        int sourceFrames)
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new LingeringPulseSound(sourceFrames));
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId)),
            Event(1, 0, new StartNoteCommand(PulseId))));
        session.ConfigurePhysicalChannelOutputFilter(0, Echo(915, 0.002));

        float[] first = new float[2];
        session.Render(0, 2, first);
        Assert.That(first, Is.EqualTo(new float[] { 1, 1 }));
        Assert.That(session.VirtualVoices,
            Has.Count.EqualTo(sourceFrames == 2 ? 0 : 1),
            "a finished virtual voice must preserve its Filter tail even " +
            "after removal from the active displaced-voice collection");
        // A displaced voice has its own copy of the output Filter history.
        // Clearing the *physical* host must not clear the virtual host's
        // pending echo from the first note.
        session.ConfigurePhysicalChannelOutputFilter(0, null);
        float[] later = new float[5];
        session.Render(2, 5, later);
        Assert.That(later, Is.EqualTo(new float[] { 1, 0, 0, 0, 0 }),
            "the dynamic NNA host must emit its cloned echo independently");
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

    [Test]
    public void SaturatedFilterObservesSummedHostPcmNotTwoSeparateVoiceResponses()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        FilterDefinition definition = new((ObjectId)951U, "Saturated", [
            new FilterStageDefinition(1, "ladder", FilterKind.LadderSaturated),
        ]);
        FilterInstance instance = new(definition);
        instance.SetStageParameters(1, [
            new("CutoffHz", FilterParameterValue.FromNumber(100)),
            new("Resonance", FilterParameterValue.FromNumber(0)),
            new("Drive", FilterParameterValue.FromNumber(10)),
            new("Oversampling", FilterParameterValue.FromChoice("1x")),
        ]);

        PlaybackSession session = Session(sounds);
        session.ConfigurePhysicalChannelOutputFilter(0, instance);
        session.ApplyScopedEvent(12, ChannelTarget.Physical(0),
            [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 12);
        session.ApplyScopedEvent(13, ChannelTarget.Physical(0),
            [new StartNoteCommand(PulseId)], physicalPlaybackOwner: 13);
        float[] pcm = new float[12];
        session.Render(0, pcm.Length, pcm);

        StreamingFilterChain combined = new(new FilterChain([instance]), Rate, 1);
        float[] expected = new float[pcm.Length];
        expected[0] = 2;
        combined.ProcessInterleaved(expected);
        Assert.That(pcm, Is.EqualTo(expected).Within(1e-6f));

        StreamingFilterChain individual = new(new FilterChain([instance]), Rate, 1);
        float[] isolated = new float[pcm.Length];
        isolated[0] = 1;
        individual.ProcessInterleaved(isolated);
        Assert.That(Math.Abs(pcm[0] - 2 * isolated[0]), Is.GreaterThan(1e-4f),
            "nonlinearity must see the single summed host signal");
    }

    [Test]
    public void FlattenedChildCutAntiClickAndSiblingShareOnePersistentHostFilter()
    {
        float[] Render(bool filter)
        {
            TestResolver sounds = new();
            sounds.Add(PulseId, new PulseSound());
            sounds.Add((ObjectId)999U, new HeldSound());
            PlaybackSession session = Session(sounds);
            if (filter)
                session.ConfigurePhysicalChannelOutputFilter(0, Echo(961, 0.002));
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
                [new BeginFlattenedSourceVolumeCommand(1, 1)]);
            session.ApplyScopedEvent(21, ChannelTarget.Physical(0),
                [new StartNoteCommand((ObjectId)999U)
                {
                    ParentSourceScopes = new long[] { 1 },
                }], physicalPlaybackOwner: 21);
            float[] pcm = new float[10];
            session.Render(0, 1, pcm.AsSpan(0, 1));
            session.ApplyFlattenedScopeAction(1, NoteDisplacementAction.Cut);
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
                [new StartNoteCommand(PulseId)]);
            session.Render(1, 9, pcm.AsSpan(1));
            return pcm;
        }

        float[] dryHost = Render(false);
        float[] wetHost = Render(true);
        StreamingFilterChain reference = new(
            new FilterChain([Echo(961, 0.002)]), Rate, 1);
        reference.ProcessInterleaved(dryHost);
        Assert.That(wetHost, Is.EqualTo(dryHost).Within(1e-6f),
            "the single host Filter processes canceled child anti-click " +
            "samples and a new sibling without scope-owned replacement");
    }

    [Test]
    public void NativeHostSelectClearAndReplacementAreOrderedAndPersistAcrossNotes()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0,
                new SetChannelFilterCommand
                {
                    Configuration = Echo(1001, 0.001),
                },
                new StartNoteCommand(PulseId)),
            Event(3, 0, new StartNoteCommand(PulseId)),
            Event(5, 0, new SetChannelFilterCommand(),
                new StartNoteCommand(PulseId))));
        float[] pcm = new float[8];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[]
        {
            1, 1, 0, 1, 1, 1, 0, 0,
        }));
    }

    [Test]
    public void CompatibleHostParameterUpdateKeepsHistoryAndSmoothedGain()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ApplyScopedEvent(9, ChannelTarget.Physical(0),
        [
            new SetChannelFilterCommand
            {
                Configuration = Echo(1002, 0.002),
            },
            new StartNoteCommand(PulseId),
        ], physicalPlaybackOwner: 9);
        float[] first = new float[1];
        session.Render(0, 1, first);
        session.ApplyScopedEvent(10, ChannelTarget.Physical(0),
        [
            new SetChannelFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
        ], physicalPlaybackOwner: 10);
        float[] rest = new float[4];
        session.Render(1, 4, rest);
        Assert.That(first, Is.EqualTo(new float[] { 1 }));
        Assert.That(rest, Is.EqualTo(new float[] { 0, 0.96875f, 0, 0 }),
            "the actual physical host shares one smoothed history across owners");
    }

    [Test]
    public void StructuralHostUpdateResetsOnlyItsOwnIncompatibleHistory()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds, Schedule(
            Event(0, 0, new StartNoteCommand(PulseId)),
            Event(0, 1, new StartNoteCommand(PulseId))));
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterCommand { Configuration = Echo(1003, 0.002) },
        ]);
        session.ApplyLiveEvent(ChannelTarget.Physical(1),
        [
            new SetChannelFilterCommand { Configuration = Echo(1004, 0.002) },
        ]);
        session.Render(0, 1, new float[1]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterParametersCommand(1, [
                new("DelaySeconds", FilterParameterValue.FromNumber(0.001)),
            ]),
        ]);
        float[] pcm = new float[3];
        session.Render(1, 3, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 0, 1, 0 }),
            "host 1 retains its delay while host 0 discards obsolete topology");
    }

    [Test]
    public void InvalidHostParameterBatchLeavesEveryFieldAndHistoryUntouched()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        List<string> warnings = [];
        session.ChannelFilterCommandRejected = warnings.Add;
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
            new SetChannelFilterCommand { Configuration = Echo(1005, 0.002) },
            new StartNoteCommand(PulseId),
        ]);
        session.Render(0, 1, new float[1]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
                new("NotARealParameter", FilterParameterValue.FromNumber(1)),
            ]),
            new SetChannelFilterCommand((ObjectId)1005U),
        ]);
        float[] later = new float[4];
        session.Render(1, 4, later);
        Assert.That(later, Is.EqualTo(new float[] { 0, 1, 0, 0 }));
        Assert.That(warnings, Has.Count.EqualTo(3),
            "absent stage, invalid partial batch and unbound ID must each diagnose");
    }

    [Test]
    public void FlattenedChildCommandTargetsRealPhysicalHostNotChildOwnerMemory()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ApplyScopedEvent(20, ChannelTarget.Physical(0),
        [
            new BeginFlattenedSourceVolumeCommand(90, 1),
            new SetChannelFilterCommand { Configuration = Echo(1006, 0.002) },
            new StartNoteCommand(PulseId)
            {
                ParentSourceScopes = new long[] { 90 },
            },
        ], physicalPlaybackOwner: 20);
        float[] first = new float[1];
        session.Render(0, 1, first);
        session.ApplyFlattenedScopeAction(90, NoteDisplacementAction.Cut);
        session.ApplyScopedEvent(21, ChannelTarget.Physical(0),
        [
            new StartNoteCommand(PulseId),
        ], physicalPlaybackOwner: 21);
        float[] rest = new float[4];
        session.Render(1, 4, rest);
        Assert.That(first[0], Is.EqualTo(1));
        Assert.That(rest[0], Is.GreaterThanOrEqualTo(1));
        Assert.That(rest[1], Is.GreaterThanOrEqualTo(1),
            "the child-selected host filter persists into unrelated owner 21");
    }

    [Test]
    public void ScopedVirtualNativeControlsDoNotCrossInvocationNamespaces()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ApplyScopedEvent(11, ChannelTarget.Virtual(7),
        [
            new SetChannelFilterCommand { Configuration = Echo(1007, 0.001) },
            new StartNoteCommand(PulseId),
        ]);
        session.ApplyScopedEvent(12, ChannelTarget.Virtual(7),
        [
            new SetChannelFilterCommand { Configuration = Echo(1008, 0.002) },
            new StartNoteCommand(PulseId),
        ]);
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
        [
            new StartNoteCommand(PulseId),
        ]);
        float[] pcm = new float[5];
        session.Render(0, 5, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 3, 1, 1, 0, 0 }));
    }

    [Test]
    public void ReplacingHostFilterStartsFreshDspAndDoesNotLeakOldDelay()
    {
        TestResolver sounds = new();
        sounds.Add(PulseId, new PulseSound());
        PlaybackSession session = Session(sounds);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterCommand { Configuration = Echo(1009, 0.002) },
            new StartNoteCommand(PulseId),
        ]);
        session.Render(0, 1, new float[1]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
        [
            new SetChannelFilterCommand { Configuration = Echo(1010, 0.001) },
        ]);
        float[] pcm = new float[4];
        session.Render(1, 4, pcm);
        Assert.That(pcm, Is.All.Zero);
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

    private sealed class LingeringPulseSound : ISound
    {
        private readonly int _sourceFrames;
        public LingeringPulseSound(int sourceFrames) => _sourceFrames = sourceFrames;
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Continue);
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => _sourceFrames;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            if (startFrame == 0 && frameCount > 0)
                destination[0] += 1;
        }
    }

    private sealed class HeldSound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Continue);
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state) => 6;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
            => destination.Fill(1f);
    }

    private sealed class TestResolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Add(ObjectId id, ISound sound) => _sounds[id] = sound;
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
            => _sounds.TryGetValue(id, out sound);
    }
}
