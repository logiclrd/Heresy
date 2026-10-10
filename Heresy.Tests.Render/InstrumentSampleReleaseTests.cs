using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class InstrumentSampleReleaseTests
{
    private static readonly ObjectId SampleId = (ObjectId)10U;
    private static readonly ObjectId InnerId = (ObjectId)11U;
    private static readonly ObjectId OuterId = (ObjectId)12U;
    private static readonly ObjectId EnvelopeId = (ObjectId)13U;

    [Test]
    public void OuterInstrumentVolumeReleaseSustainsSampleChosenByInnerInstrument()
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample());
        InstrumentSound inner = Instrument(InnerId, SampleId, null, sounds);
        sounds.Add(InnerId, inner);
        InstrumentSound outer = Instrument(OuterId, InnerId,
            TimeSpan.FromMilliseconds(4), sounds);
        sounds.Add(OuterId, outer);

        PlaybackSession session = Session(
            Schedule(
                Event(0, 0, new StartNoteCommand(OuterId)),
                Event(2, 0, new NoteOffCommand())),
            sounds);
        float[] pcm = new float[9];

        session.Render(0, 9, pcm);

        Assert.That(pcm,
            Is.EqualTo(new float[] { 1f, 1f, 1f, 0.75f, 0.5f,
                0.25f, 0f, 0f, 0f }).Within(1e-6f));
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null,
            "The Instrument's release must retire its terminal sample at frame 6.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectOrUnenvelopedInstrumentSampleStillCutsAtNoteOff(
        bool useInstrument)
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample());
        if (useInstrument)
            sounds.Add(InnerId, Instrument(InnerId, SampleId, null, sounds));

        PlaybackSession session = Session(
            Schedule(
                Event(0, 0, new StartNoteCommand(
                    useInstrument ? InnerId : SampleId)),
                Event(2, 0, new NoteOffCommand())),
            sounds);
        float[] pcm = new float[7];

        session.Render(0, pcm.Length, pcm);

        Assert.That(pcm,
            Is.EqualTo(new float[] { 1f, 1f, 0f, 0f, 0f, 0f, 0f }));
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
    }

    [Test]
    public void DirectAndInstrumentInvocationsOfSameSampleHaveIndependentNoteOffPolicies()
    {
        TestSoundResolver sounds = new();
        SampleSound commonSample = LoopingSample();
        sounds.Add(SampleId, commonSample);
        sounds.Add(OuterId, Instrument(OuterId, SampleId,
            TimeSpan.FromMilliseconds(4), sounds));

        PlaybackSession session = Session(
            Schedule(
                Event(0, 0, new StartNoteCommand(SampleId)),
                Event(0, 1, new StartNoteCommand(OuterId)),
                Event(2, 0, new NoteOffCommand()),
                Event(2, 1, new NoteOffCommand())),
            sounds);
        float[] pcm = new float[8];

        session.Render(0, pcm.Length, pcm);

        Assert.That(pcm,
            Is.EqualTo(new float[] { 2f, 2f, 1f, 0.75f,
                0.5f, 0.25f, 0f, 0f }).Within(1e-6f),
            "The direct sample must cut on Note Off while the same " +
            "SampleSound object's Instrument invocation still releases.");
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
        Assert.That(session.GetChannelState(1).CurrentVoice, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroReleaseInstrumentSampleOffRetiresAtExactFrame(
        bool virtualTarget)
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample());
        sounds.Add(OuterId, Instrument(OuterId, SampleId,
            TimeSpan.Zero, sounds));
        PlaybackSession session = Session(Schedule(), sounds);
        ChannelTarget target = virtualTarget
            ? ChannelTarget.Virtual(7)
            : ChannelTarget.Physical(0);

        session.ApplyLiveEvent(target, [new StartNoteCommand(OuterId)]);
        float[] beforeOff = new float[2];
        session.Render(0, 2, beforeOff);
        Assert.That(beforeOff, Is.EqualTo(new float[] { 1f, 1f }));

        session.ApplyLiveEvent(target, [new NoteOffCommand()]);
        if (!virtualTarget)
            Assert.That(session.GetChannelState(0).CurrentVoice,
                Is.Null, "A zero-release voice must retire at the Off frame.");

        session.EndInput();
        Assert.That(session.IsQuiescent, Is.True,
            "The finished physical/virtual sample must not linger until a later render call.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroReleaseInstrumentSampleRetiresAtEndInputWithoutExtraRender(
        bool virtualTarget)
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample());
        sounds.Add(OuterId, Instrument(OuterId, SampleId,
            TimeSpan.Zero, sounds));
        PlaybackSession session = Session(Schedule(), sounds);
        ChannelTarget target = virtualTarget
            ? ChannelTarget.Virtual(7)
            : ChannelTarget.Physical(0);

        session.ApplyLiveEvent(target, [new StartNoteCommand(OuterId)]);
        session.Render(0, 2, new float[2]);
        session.EndInput();

        Assert.That(session.IsQuiescent, Is.True,
            "EndInput must synchronously retire a zero-release Instrument sample.");
        if (!virtualTarget)
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
    }

    [Test]
    public void ZeroReleaseNnaOffDoesNotRetainFinishedDisplacedInstrumentSample()
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample(NewNotePolicy.Off));
        sounds.Add(OuterId, Instrument(OuterId, SampleId,
            TimeSpan.Zero, sounds));

        PlaybackSession session = Session(Schedule(), sounds);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(OuterId)]);
        session.Render(0, 2, new float[2]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(OuterId)]);

        Assert.That(session.VirtualVoices, Is.Empty,
            "NNA Off with zero envelope release must not migrate an " +
            "already-finished voice into the displaced-voice pool.");
        float[] next = new float[2];
        session.Render(2, 2, next);
        Assert.That(next, Is.EqualTo(new float[] { 1f, 1f }));
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null,
            "The replacement remains live after immediate retirement.");
    }

    [Test]
    public void NnaOffMigratedInstrumentSampleFinishesVolumeReleaseAlongsideReplacement()
    {
        TestSoundResolver sounds = new();
        sounds.Add(SampleId, LoopingSample(NewNotePolicy.Off));
        sounds.Add(OuterId, Instrument(OuterId, SampleId,
            TimeSpan.FromMilliseconds(4), sounds));

        PlaybackSession session = Session(
            Schedule(
                Event(0, 0, new StartNoteCommand(OuterId)),
                Event(2, 0, new StartNoteCommand(OuterId))),
            sounds);
        float[] pcm = new float[9];

        session.Render(0, 4, pcm.AsSpan(0, 4));
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1),
            "The first sample must migrate and release when displaced.");
        session.Render(4, 5, pcm.AsSpan(4, 5));

        Assert.That(pcm, Is.EqualTo(new float[]
        {
            1f, 1f, 2f, 1.75f, 1.5f, 1.25f, 1f, 1f, 1f,
        }).Within(1e-6f));
        Assert.That(session.VirtualVoices, Is.Empty,
            "The migrated old sample retires at its own ADSR release end.");
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null,
            "The replacement remains playing after the old release ends.");
    }

    private static SampleSound LoopingSample(NewNotePolicy? policy = null)
    {
        SampleDefinition definition = new(
            SampleId, "PCM", new ExternalAssetReference("pcm.raw"))
        {
            Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4),
        };
        return new SampleSound(
            definition, new MemorySampleData(1000, 1, [1f, 1f, 1f, 1f]),
            policy);
    }

    private static InstrumentSound Instrument(
        ObjectId id, ObjectId selected, TimeSpan? release,
        TestSoundResolver sounds)
    {
        InstrumentDefinition definition = new(id, "Instrument");
        ToneSpecification tone;
        TestEnvelopeResolver envelopes = new();
        if (release.HasValue)
        {
            envelopes.Add(EnvelopeId, new AdsrEnvelopeCurve(
                new AdsrEnvelopeDefinition(EnvelopeId, "Volume")
                {
                    SustainLevel = 1.0,
                    Release = release.Value,
                }));
            tone = new ToneSpecification
            {
                SourceId = selected,
                VolumeEnvelopeId = EnvelopeId,
            };
        }
        else
            tone = new ToneSpecification { SourceId = selected };

        definition.ToneSpecifications.Add(tone);
        definition.ToneTable.Add(0);
        return new InstrumentSound(definition, sounds, envelopes);
    }

    private static PlaybackSession Session(
        NoteSchedule schedule, ISoundResolver sounds)
        => new(
            new RenderContext(new RenderConfiguration(
                1000, [new OutputChannelConfiguration(
                    Vector3.Zero, positionalImportance: 0)])),
            schedule, sounds);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder builder = new();
        foreach (NoteEvent e in events)
            builder.Append(e);
        return builder.Freeze();
    }

    private static NoteEvent Event(
        int milliseconds, int channel, params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(milliseconds), 0),
            ChannelTarget.Physical(channel), commands);

    private sealed class TestSoundResolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];

        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);

        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = !mixdown && _sounds.TryGetValue(id, out ISound? resolved)
                ? resolved : null;
            return sound is not null;
        }
    }

    private sealed class TestEnvelopeResolver : IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, IEnvelopeCurve> _curves = [];

        public void Add(ObjectId id, IEnvelopeCurve curve)
            => _curves.Add(id, curve);

        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
            => _curves.TryGetValue(id, out curve);
    }
}
