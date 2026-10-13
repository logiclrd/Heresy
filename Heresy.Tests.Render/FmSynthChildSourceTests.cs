using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthChildSourceTests
{
    private static readonly double C4 = 440.0 * Math.Pow(2.0, -9.0 / 12.0);

    [Test]
    public void SourceResolutionOccursAtPreparationAndInvocationIsOwnedPerVoice()
    {
        ObjectId id = (ObjectId)4U;
        CountingSound child = new();
        Resolver resolver = new((id, child));
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, id, C4),
        ], 0), resolver, soundResolver: resolver);
        Assert.That(resolver.Calls, Is.EqualTo(1));
        SoundInvocation first = synth.CreateInvocation(1, 1);
        SoundInvocation second = synth.CreateInvocation(2, 1);
        Assert.That(child.Invocations, Is.EqualTo(2));
        RenderContext context = Mono(8);
        float[] a = new float[4], b = new float[4];
        synth.Render(context, first.State, 0, 4, a);
        synth.Render(context, second.State, 0, 4, b);
        Assert.Multiple(() =>
        {
            Assert.That(resolver.Calls, Is.EqualTo(1));
            Assert.That(child.Invocations, Is.EqualTo(2));
            Assert.That(a, Is.EqualTo(new float[] { 1, 2, 3, 4 }));
            Assert.That(b, Is.EqualTo(new float[] { 2, 4, 6, 8 }));
        });
        Assert.That(() => synth.Render(context, first.State, 0, 1, new float[1]),
            Throws.TypeOf<InvalidOperationException>(),
            "audio callbacks must not rebuild child invocations on an unprepared seek");
        int priorInvocations = child.Invocations;
        synth.PrepareReplay(context, first.State, targetFrame: 2);
        Assert.That(child.Invocations, Is.EqualTo(priorInvocations + 1),
            "control-side replay creates exactly one new child invocation");
        float[] replay = new float[2];
        synth.Render(context, first.State, 2, 2, replay);
        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.EqualTo(new float[] { 3, 4 }));
            Assert.That(resolver.Calls, Is.EqualTo(1),
                "replay must use the already-compiled child ISound");
            Assert.That(second.State, Is.Not.SameAs(first.State),
                "rebuilding one voice must not mutate another");
        });
        float[] independent = new float[2];
        synth.Render(context, second.State, 4, independent.Length, independent);
        Assert.That(independent, Is.EqualTo(new float[] { 10, 12 }),
            "seeking another note never rewinds this voice's child state");
    }

    [Test]
    public void DirectSampleReplayAcrossFormatChangesMatchesFreshState()
    {
        ObjectId id = (ObjectId)301U;
        SampleSound sample = new(new SampleDefinition(id, "PCM",
            new ExternalAssetReference("rates.wav")),
            new MemorySampleData(8, 1, new float[] { 0, 1, 2, 3 }));
        Resolver resolver = new((id, sample));
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, id, C4),
        ], 0), resolver, soundResolver: resolver);
        SoundState changing = synth.CreateState();
        float[] original = new float[6];
        synth.Render(Mono(8), changing, 0, 6, original);
        Assert.That(original, Is.EqualTo(new float[] { 0, 1, 2, 3, 0, 0 }));
        Assert.That(() => synth.Render(Mono(16), changing, 6, 1,
            new float[1]), Throws.TypeOf<InvalidOperationException>(),
            "a live format change is not a PCM callback operation");

        SoundState fresh = synth.CreateState();
        float[] expected = new float[12];
        synth.Render(Mono(16), fresh, 0, 12, expected);
        synth.PrepareReplay(Mono(16), changing, targetFrame: 3);
        float[] afterSwitch = new float[9];
        synth.Render(Mono(16), changing, 3, 9, afterSwitch);
        Assert.That(afterSwitch, Is.EqualTo(expected.AsSpan(3).ToArray()).Within(1e-6f));
        synth.PrepareReplay(Mono(8), changing, targetFrame: 0);
        float[] returned = new float[6];
        synth.Render(Mono(8), changing, 0, 6, returned);
        Assert.Multiple(() =>
        {
            Assert.That(returned, Is.EqualTo(original));
            Assert.That(resolver.Calls, Is.EqualTo(1),
                "no re-resolution occurs across format-specific invocation rebuilds");
        });
    }

    [Test]
    public void FrozenPreparedSourceRejectsRateChangesWithoutDiscardingItsState()
    {
        ObjectId id = (ObjectId)302U;
        SampleSound sample = new(new SampleDefinition(id, "Frozen",
            new ExternalAssetReference("frozen.wav")),
            new MemorySampleData(8, 1, new float[] { 1, 2, 3, 4 }));
        Resolver resolver = new((id, sample));
        PreparedToneFilterStageFactory prepared = new(8);
        prepared.Freeze();
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, id, C4),
        ], 0), resolver, soundResolver: resolver,
            preparedChildFilterFactory: prepared);
        SoundState state = synth.CreateState();
        float[] first = new float[2];
        synth.Render(Mono(8), state, 0, 2, first);
        Assert.That(first, Is.EqualTo(new float[] { 1, 2 }));
        Assert.That(() => synth.PrepareReplay(Mono(16), state, 0),
            Throws.TypeOf<InvalidOperationException>(),
            "frozen FIR plans are sample-rate-specific and cannot be reused");
        float[] continued = new float[2];
        synth.Render(Mono(8), state, 2, 2, continued);
        Assert.That(continued, Is.EqualTo(new float[] { 3, 4 }),
            "a rejected seek must leave the original frozen voice intact");
    }

    [Test]
    public void ConstantPitchIsIndependentOfOuterNoteAndSpeed()
    {
        ObjectId id = (ObjectId)3U;
        CountingSound child = new();
        Resolver resolver = new((id, child));
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(1, id, C4 * 2, constant: true),
        ], 1), resolver, soundResolver: resolver);
        SoundInvocation state = synth.CreateInvocation(3, 1.5);
        float[] output = new float[2];
        synth.Render(Mono(4), state.State, 0, 2, output);
        Assert.That(output, Is.EqualTo(new float[] { 2, 4 }));
    }

    [Test]
    public void StereoSampleUsesArithmeticMonoDownmixThenParentSpatialization()
    {
        ObjectId id = (ObjectId)3U;
        SampleDefinition definition = new(id, "Stereo",
            new ExternalAssetReference("stereo.wav"));
        SampleSound sample = new(definition, new MemorySampleData(4, 2,
            new float[] { 1, -0.5f, 0.5f, 0.5f, -1, 1 }));
        Resolver resolver = new((id, sample));
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, id, C4),
        ], 0), resolver, soundResolver: resolver);
        SoundInvocation inv = synth.CreateInvocation(1, 1);
        RenderContext stereo = new(RenderConfiguration.Stereo(4));
        float[] output = new float[6];
        synth.Render(stereo, inv.State, 0, 3, output);
        // A centre-positioned Synth is distributed at 1/sqrt(2) to
        // each stereo output; its child PCM has already been folded to
        // mono, so it is not independently spatialized twice.
        float parentGain = (float)(1 / Math.Sqrt(2));
        Assert.Multiple(() =>
        {
            Assert.That(output[0], Is.EqualTo(0.25f * parentGain).Within(1e-6f));
            Assert.That(output[1], Is.EqualTo(0.25f * parentGain).Within(1e-6f));
            Assert.That(output[2], Is.EqualTo(0.5f * parentGain).Within(1e-6f));
            Assert.That(output[3], Is.EqualTo(0.5f * parentGain).Within(1e-6f));
            Assert.That(output[4], Is.EqualTo(0).Within(1e-6f));
            Assert.That(output[5], Is.EqualTo(0).Within(1e-6f));
        });
    }

    [Test]
    public void NestedInstrumentFlattensItsSampleAndAppliesCapturedVolumeEnvelope()
    {
        ObjectId instrumentId = (ObjectId)8U, sampleId = (ObjectId)9U;
        ObjectId envelopeId = (ObjectId)10U;
        SampleSound sample = new(new SampleDefinition(sampleId, "P",
            new ExternalAssetReference("p.wav")),
            new MemorySampleData(4, 1, new float[] { 1, 1, 1, 1 }));
        Resolver resolver = new();
        InstrumentSound instrument = new(
            pitch => new ToneSpecification {
                SourceId = sampleId, VolumeEnvelopeId = envelopeId },
            resolver, resolver);
        resolver.Add(sampleId, sample);
        resolver.Add(instrumentId, instrument);
        resolver.AddEnvelope(envelopeId, new HalfCurve());
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, instrumentId, C4),
        ], 0), resolver, soundResolver: resolver);
        SoundInvocation voice = synth.CreateInvocation(1, 1);
        float[] output = new float[4];
        synth.Render(Mono(4), voice.State, 0, 4, output);
        Assert.Multiple(() => {
            Assert.That(output, Is.EqualTo(new float[] { 0.5f, 0.5f, 0.5f, 0.5f }));
            Assert.That(resolver.Calls, Is.EqualTo(2),
                "one lookup during Synth preparation, one at Instrument invocation");
        });
    }

    [Test]
    public void UnassignedAndMissingSourceProduceSilenceWithoutPcmResolverCalls()
    {
        Resolver resolver = new();
        FmSynthSound synth = new(new FmSynthGraph([
            new FmSoundSourceNode(0, ObjectId.None, C4),
            new FmSoundSourceNode(1, (ObjectId)555U, C4),
            new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
        ], 2), resolver, soundResolver: resolver);
        SoundState state = synth.CreateState();
        float[] result = [1, 1];
        synth.Render(Mono(4), state, 0, 2, result);
        Assert.That(result, Is.EqualTo(new float[] { 1, 1 }));
        Assert.That(resolver.Calls, Is.EqualTo(1));
    }

    [Test]
    public void PreparedSourceAcceptsConnectedGraphModulationWithoutPcmResolution()
    {
        ObjectId id = (ObjectId)8U;
        CountingSound child = new();
        Resolver resolver = new((id, child));
        FmSynthSound sound = new(new FmSynthGraph([
            new FmConstantNode(0, 2),
            new FmSoundSourceNode(1, id, C4, multiplierNodeId: 0),
        ], 1), resolver, soundResolver: resolver);
        Assert.That(resolver.Calls, Is.EqualTo(1));
        float[] mono = new float[3];
        sound.Render(Mono(8), sound.CreateState(), 0, 3, mono);
        Assert.That(mono, Is.EqualTo(new float[] { 1, 2, 3 }));
        Assert.That(resolver.Calls, Is.EqualTo(1),
            "modulation must use the prepared child rather than per-frame lookup");
    }

    private static RenderContext Mono(int rate) => new(
        new RenderConfiguration(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private sealed class HalfCurve : IEnvelopeCurve
    {
        public double GetValue(long activeFrame, int sampleRate,
            long? noteOffActiveFrame) => 0.5;
    }

    private sealed class CountingSound : ISound
    {
        private sealed class State : SoundState { }
        public int Invocations { get; private set; }
        public SoundState CreateState() => new State();
        public NoteConfigurationSnapshot SnapshotNoteConfiguration() =>
            NoteConfigurationSnapshot.Default;
        public SoundInvocation CreateInvocation(double pitchMultiplier,
            double playbackSpeedMultiplier)
        {
            Invocations++;
            SoundState state = CreateState();
            state.PitchMultiplier = pitchMultiplier;
            state.PlaybackSpeedMultiplier = playbackSpeedMultiplier;
            return new SoundInvocation(this, state, SnapshotNoteConfiguration());
        }
        public long? GetEndFrameExclusive(RenderContext context, SoundState state) => null;
        public void Render(RenderContext context, SoundState state, long startFrame,
            int frameCount, Span<float> destination)
        {
            for (int i = 0; i < frameCount; i++)
                destination[i] += (float)((startFrame + i + 1)
                    * state.PitchMultiplier * state.PlaybackSpeedMultiplier);
        }
    }

    private sealed class Resolver : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        private readonly Dictionary<ObjectId, IEnvelopeCurve> _envelopes = [];
        public int Calls { get; private set; }

        public Resolver(params (ObjectId Id, ISound Sound)[] sounds)
        {
            foreach ((ObjectId id, ISound sound) in sounds)
                Add(id, sound);
        }
        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);
        public void AddEnvelope(ObjectId id, IEnvelopeCurve envelope) =>
            _envelopes.Add(id, envelope);
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            Calls++;
            return _sounds.TryGetValue(sourceId, out sound);
        }
        public bool TryResolve(ObjectId envelopeId, out IEnvelopeCurve? curve) =>
            _envelopes.TryGetValue(envelopeId, out curve);
    }
}
