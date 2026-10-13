using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

/// <summary>
/// A prepared ISound-backed Synth is a physical/virtual PlaybackVoice:
/// NNA must move its entire child invocation and live graph Filter history,
/// then reclaim the displaced voice at its observed finite boundary.
/// </summary>
[TestFixture]
public sealed class FmSynthNnaVirtualLifecycleTests
{
    private const int Rate = 1000;
    private static readonly double C4 = 440 * Math.Pow(2.0, -9.0 / 12.0);
    private static readonly ObjectId SampleId = (ObjectId)72001U;
    private static readonly ObjectId InstrumentId = (ObjectId)72002U;
    private static readonly ObjectId InnerId = (ObjectId)72003U;
    private static readonly ObjectId SourceId = (ObjectId)72004U;
    private static readonly ObjectId EnvelopeId = (ObjectId)72005U;

    [TestCase(NewNoteAction.Cut, false)]
    [TestCase(NewNoteAction.Continue, true)]
    [TestCase(NewNoteAction.Off, true)]
    [TestCase(NewNoteAction.Fade, true)]
    public void PhysicalNnaDisplacesNestedFiniteSourceAndReclaimsLongBlock(
        NewNoteAction action, bool migrated)
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: false,
            action: action, inheritedEnvelope: false);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, ChannelTarget.Physical(0), new StartNoteCommand(SourceId)),
            Event(2, ChannelTarget.Physical(0), new StartNoteCommand(SourceId))));

        float[] whole = new float[32];
        session.Render(0, 3, whole.AsSpan(0, 3));
        PlaybackVoice? current = session.GetChannelState(0).CurrentVoice;
        Assert.That(current, Is.Not.Null);
        Assert.That(current!.StartFrame, Is.EqualTo(2));
        Assert.That(session.VirtualVoices.Count, Is.EqualTo(migrated ? 1 : 0));
        if (migrated)
        {
            PlaybackVoice old = session.VirtualVoices.Single();
            Assert.Multiple(() =>
            {
                Assert.That(old.StartFrame, Is.Zero);
                Assert.That(old.SoundState, Is.Not.SameAs(current.SoundState),
                    "NNA must preserve the old per-voice graph and child histories");
                Assert.That(old.Sound, Is.SameAs(current.Sound),
                    "prepared Synth coefficients may be shared, never voice state");
                Assert.That(old.DisplacedOutputFilterState,
                    Is.Null, "no host Filter has been selected to fork");
            });
            if (action == NewNoteAction.Off)
                Assert.That(old.SoundState.NoteOffTime,
                    Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            if (action == NewNoteAction.Fade)
                Assert.That(old.FadeEndFrameExclusive, Is.EqualTo(6));
        }

        int lookupsAfterBinding = resolver.SoundLookups;
        session.Render(3, 29, whole.AsSpan(3, 29));
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty,
                "one long block must retire all finished NNA sources immediately");
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(whole.Skip(10), Is.All.Zero);
            Assert.That(resolver.SoundLookups, Is.EqualTo(lookupsAfterBinding),
                "no asset resolution may occur during settled PCM rendering");
        });

        Registry otherResolver = new();
        FmSynthSound otherSynth = BuildNested(otherResolver, loop: false,
            action: action, inheritedEnvelope: false);
        otherResolver.Add(SourceId, otherSynth);
        PlaybackSession split = Session(otherResolver, Schedule(
            Event(0, ChannelTarget.Physical(0), new StartNoteCommand(SourceId)),
            Event(2, ChannelTarget.Physical(0), new StartNoteCommand(SourceId))));
        float[] pieces = new float[whole.Length];
        int[] blocks = [1, 1, 2, 3, 1, 8, 16];
        int frame = 0;
        foreach (int count in blocks)
        {
            split.Render(frame, count, pieces.AsSpan(frame, count));
            frame += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(frame, Is.EqualTo(32));
            Assert.That(pieces, Is.EqualTo(whole).Within(1e-6f),
                "displaced graph Filter histories must be chunk-invariant");
            Assert.That(split.VirtualVoices, Is.Empty);
            Assert.That(split.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(otherResolver.SoundLookups,
                Is.EqualTo(resolver.SoundLookups));
        });
    }

    [TestCase(NewNoteAction.Off)]
    [TestCase(NewNoteAction.Fade)]
    public void VirtualNnaPreservesLoopingInstrumentReleaseAndFilterHistory(
        NewNoteAction action)
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: true,
            action: action, inheritedEnvelope: true);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        float[] output = new float[36];
        session.Render(0, 4, output.AsSpan(0, 4));
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = session.VirtualVoices.Single();
        Assert.That(displaced.SoundState, Is.Not.Null);
        if (action == NewNoteAction.Off)
            Assert.That(displaced.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(4)));
        else
            Assert.That(displaced.FadeEndFrameExclusive, Is.EqualTo(8));

        session.Render(4, 2, output.AsSpan(4, 2));
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1),
            "the old loop survives its displacement while releasing or fading");
        session.ApplyLiveEvent(ChannelTarget.Virtual(7), [new NoteOffCommand()]);
        int boundLookups = resolver.SoundLookups;
        session.Render(6, 30, output.AsSpan(6, 30));
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty,
                "a long block must not retain a released looping NNA Synth");
            Assert.That(output.Skip(20), Is.All.Zero,
                "neither the volume Envelope nor graph Filter may leak indefinitely");
            Assert.That(resolver.SoundLookups, Is.EqualTo(boundLookups));
        });
    }

    [Test]
    public void SameVirtualChannelIdInTwoScopesKeepsIndependentSynthVoices()
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: false,
            action: NewNoteAction.Continue, inheritedEnvelope: false);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        float[] output = new float[30];
        session.Render(0, 2, output.AsSpan(0, 2));
        Assert.That(output[0], Is.GreaterThan(0),
            "two independent scoped source voices are audible");

        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice old = session.VirtualVoices.Single();
        Assert.Multiple(() =>
        {
            Assert.That(old.OriginScopedVirtualOwner, Is.EqualTo(101));
            Assert.That(old.OriginVirtualChannelId, Is.EqualTo(7u));
        });
        int lookups = resolver.SoundLookups;
        session.Render(2, 28, output.AsSpan(2, 28));
        Assert.Multiple(() =>
        {
            Assert.That(output[2], Is.GreaterThan(output[0]),
                "the new scope-101 note adds a third, separately filtered voice");
            Assert.That(output.Skip(10), Is.All.Zero);
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(resolver.SoundLookups, Is.EqualTo(lookups));
        });
    }

    private static FmSynthSound BuildNested(Registry registry, bool loop,
        NewNoteAction action, bool inheritedEnvelope)
    {
        SampleDefinition sampleDefinition = new(SampleId, "PCM",
            new ExternalAssetReference("source.wav"));
        if (loop)
            sampleDefinition.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
        registry.Add(SampleId, new SampleSound(sampleDefinition,
            new MemorySampleData(Rate, 1, [0.5f, 0.5f, 0.5f, 0.5f])));
        ObjectId innerSource = SampleId;
        if (inheritedEnvelope)
        {
            registry.AddEnvelope(EnvelopeId, new ThreeFrameRelease());
            registry.Add(InstrumentId, new InstrumentSound(
                _ => new ToneSpecification
                {
                    SourceId = SampleId, VolumeEnvelopeId = EnvelopeId,
                }, registry, registry));
            innerSource = InstrumentId;
        }
        FmSynthSound inner = new(new FmSynthGraph([
            new FmSoundSourceNode(0, innerSource, C4),
        ], 0), registry, soundResolver: registry);
        registry.Add(InnerId, inner);
        FilterDefinition definition = new((ObjectId)72006U, "Graph pole");
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(150));
        return new FmSynthSound(new FmSynthGraph([
            new FmSoundSourceNode(0, InnerId, C4),
            new FmFilterNode(1, 0, filter, tailSeconds: 0.002),
        ], 1), registry,
            new NewNotePolicy(action, action == NewNoteAction.Fade
                ? TimeSpan.FromMilliseconds(4) : TimeSpan.Zero),
            soundResolver: registry);
    }

    private static PlaybackSession Session(Registry registry, NoteSchedule schedule)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), schedule, registry);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder builder = new();
        foreach (NoteEvent e in events)
            builder.Append(e);
        return builder.Freeze();
    }

    private static NoteEvent Event(int frame, ChannelTarget target,
        params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(frame), 0),
            target, commands);

    private sealed class ThreeFrameRelease : IEnvelopeCurve
    {
        public double GetValue(long frame, int sampleRate, long? off)
            => !off.HasValue ? 1 : Math.Max(0, 1 - (frame - off.Value) / 3.0);

        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 3;
    }

    private sealed class Registry : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        private readonly Dictionary<ObjectId, IEnvelopeCurve> _envelopes = [];
        public int SoundLookups { get; private set; }
        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);
        public void AddEnvelope(ObjectId id, IEnvelopeCurve envelope) =>
            _envelopes.Add(id, envelope);
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            SoundLookups++;
            return !mixdown && _sounds.TryGetValue(id, out sound);
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? envelope)
            => _envelopes.TryGetValue(id, out envelope);
    }
}
