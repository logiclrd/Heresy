using System;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Samples;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthMultiTests
{
    [Test]
    public void GainCompensationMeansArithmeticAverageNotAutomaticSummation()
    {
        FmSynthGraph avg = new([
            new FmConstantNode(1, 0.5),
            new FmMultiNode(2, 1, voiceCount: 7,
                randomizePhase: false, gainCompensation: true),
        ], 2);
        FmSynthGraph sum = new([
            new FmConstantNode(1, 0.5),
            new FmMultiNode(2, 1, voiceCount: 7,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        Render(avg, 128).Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6);
        Render(sum, 128).Should().OnlyContain(x => Math.Abs(x - 3.5f) < 1e-6);
    }

    [Test]
    public void HarmonicAndChordRetuneIndependentOscillatorInstances()
    {
        foreach (FmMultiNode node in new[]
        {
            new FmMultiNode(2, 1, FmMultiDistribution.Harmonic,
                harmonicComponents: "1 2", randomizePhase: false,
                gainCompensation: false),
            new FmMultiNode(2, 1, FmMultiDistribution.Chord,
                chordComponents: "0 12", randomizePhase: false,
                gainCompensation: false),
        })
        {
            FmSynthGraph graph = new([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 100),
                node,
            ], 2);
            float[] output = Render(graph, 512);
            for (int n = 0; n < 512; n++)
            {
                double expected = Math.Sin(2 * Math.PI * 100 * n / 48000)
                    + Math.Sin(2 * Math.PI * 200 * n / 48000);
                output[n].Should().BeApproximately((float)expected, 3e-4f);
            }
        }
    }

    [Test]
    public void NestedMultiCreatesIndependentSubgraphInvocations()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, FmMultiDistribution.Harmonic,
                harmonicComponents: "1 2 3", randomizePhase: false,
                gainCompensation: false),
            new FmMultiNode(3, 2, FmMultiDistribution.Chord,
                chordComponents: "0 7", randomizePhase: false,
                gainCompensation: false),
        ], 3);
        Render(graph, 32).Should()
            .OnlyContain(x => Math.Abs(x - 1.5f) < 1e-6);
    }

    [Test]
    public void RandomizedWholeSubgraphOffsetsAndFadesAreDeterministic()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 4096.0, randomizePhase: true,
                gainCompensation: true),
        ], 2);
        float[] first = Render(graph, 4096, seed: 123);
        float[] replay = Render(graph, 4096, seed: 123);
        first.Should().Equal(replay);
        first[0].Should().BeLessThan(1.0f,
            "skipped subgraphs have a voice-local fade at the common note onset");
        first.Last().Should().BeApproximately(1.0f, 1e-6f);
        float[] alternateSeed = Render(graph, 4096, seed: 456);
        first.SequenceEqual(alternateSeed).Should().BeFalse();
    }

    [Test]
    public void DownstreamEnvelopeReleaseKeepsWrappedOscillatorAudible()
    {
        ObjectId envelopeId = (ObjectId)801U;
        FmSynthGraph graph = new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Square, 50),
            new FmMultiNode(2, 1, voiceCount: 1, randomizePhase: false),
            new FmEnvelopeNode(3, envelopeId),
            new FmOperatorNode(4, FmOperatorKind.Multiply, [2, 3]),
        ], 4);
        FmSynthSound sound = new(graph, new TenFrameRelease(envelopeId));
        SoundState state = sound.CreateState();
        state.NoteOffTime = TimeSpan.FromSeconds(3.0 / 1000);
        RenderContext context = new(new RenderConfiguration(1000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        float[] output = new float[20];
        sound.Render(context, state, 0, output.Length, output);
        Math.Abs(output[8]).Should().BeGreaterThan(0.1f,
            "an audible downstream release envelope must keep the internal oscillator alive");
        Math.Abs(output[12]).Should().BeGreaterThan(0.1f);
        output[13].Should().Be(0);
        output[18].Should().Be(0);
    }

    [Test]
    public void SampleBackedSquareWaveReceivesRealIndependentOffsetsAndReplaysExactly()
    {
        ObjectId sampleId = (ObjectId)408U;
        // The PCM carries a 100-sample square-wave cycle even though no
        // oscillator node exposes that period to the graph.
        float[] pcm = Enumerable.Range(0, 4000)
            .Select(i => i % 100 < 50 ? 1.0f : -1.0f).ToArray();
        SampleSound sample = new(new SampleDefinition(sampleId, "PCM square",
            new ExternalAssetReference("square.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(sampleId, sample);
        FmSynthGraph alignedGraph = new([
            new FmSoundSourceNode(1, sampleId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 100, randomizePhase: false,
                gainCompensation: true),
        ], 2);
        FmSynthGraph randomizedGraph = new([
            new FmSoundSourceNode(1, sampleId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 100, randomizePhase: true,
                gainCompensation: true),
        ], 2);
        FmSynthSound aligned = new(alignedGraph, resolver, soundResolver: resolver);
        FmSynthSound randomized = new(randomizedGraph, resolver, soundResolver: resolver);
        float[] withoutRandomization = new float[400];
        aligned.Render(Mono(), aligned.CreateState(), 0, 400, withoutRandomization);
        withoutRandomization[0].Should().BeApproximately(1.0f, 1e-6f);
        SoundState voice = randomized.CreateState();
        voice.SetNoiseSeed(42);
        float[] withRandomization = new float[400];
        randomized.Render(Mono(), voice, 0, 400, withRandomization);
        withRandomization[0].Should().BeLessThan(0.5f);
        // Reconstructed independent sample playheads must follow the same
        // exact sub-frame pitch integration and time displacement.
        randomized.PrepareReplay(Mono(), voice, targetFrame: 200);
        float[] replay = new float[200];
        randomized.Render(Mono(), voice, 200, replay.Length, replay);
        replay.Should().Equal(withRandomization.AsSpan(200).ToArray());
    }

    [Test]
    public void ConstituentNoteOffDoesNotMoveWithTheInitialSkip()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, voiceCount: 3, randomizePhase: true,
                cycleLength: 128),
        ], 2);
        FmSynthSound sound = new(graph, new EmptyEnvelopes());
        SoundState state = sound.CreateState();
        state.SetNoiseSeed(17);
        state.NoteOffTime = TimeSpan.FromSeconds(256.0 / 48000);
        float[] output = new float[280];
        sound.Render(Mono(), state, 0, output.Length, output);
        output[275].Should().Be(0.0f);
    }

    [Test]
    public void LiveMultiCountGrowthAndRetirementCrossfadeWithoutRebuildingChildVoices()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);

        RenderContext context = Mono();
        FmSynthSound current = new(Graph(2), new EmptyEnvelopes());
        SoundState voice = current.CreateState();
        float[] before = new float[80];
        current.Render(context, voice, 0, 80, before);
        before.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f);

        FmSynthSound grown = new(Graph(4), new EmptyEnvelopes());
        current.CanAdoptRunningState(grown, voice).Should().BeTrue();
        current.TryPrepareRunningState(grown, voice).Should().BeTrue();
        current.CommitPreparedRunningState(grown, voice);
        float[] growth = new float[130];
        grown.Render(context, voice, 80, growth.Length, growth);
        growth[0].Should().BeApproximately(0.5f, 1e-6f,
            "new children must fade in, not start at full output");
        growth[32].Should().BeInRange(0.5f, 1.0f);
        growth[100].Should().BeApproximately(1.0f, 1e-6f);

        FmSynthSound shrunk = new(Graph(1), new EmptyEnvelopes());
        grown.TryPrepareRunningState(shrunk, voice).Should().BeTrue();
        grown.CommitPreparedRunningState(shrunk, voice);
        float[] shrink = new float[130];
        shrunk.Render(context, voice, 210, shrink.Length, shrink);
        shrink[0].Should().BeApproximately(1.0f, 1e-6f);
        shrink[32].Should().BeInRange(0.25f, 1.0f);
        shrink[100].Should().BeApproximately(0.25f, 1e-6f);

        // Previously retired children keep advancing their own states
        // silently, so reactivating within the original reserve needs no
        // note restart, child allocation, or catch-up replay.
        FmSynthSound revived = new(Graph(3), new EmptyEnvelopes());
        shrunk.TryPrepareRunningState(revived, voice).Should().BeTrue();
        shrunk.CommitPreparedRunningState(revived, voice);
        float[] revival = new float[130];
        revived.Render(context, voice, 340, revival.Length, revival);
        revival[0].Should().BeApproximately(0.25f, 1e-6f);
        revival[100].Should().BeApproximately(0.75f, 1e-6f);
    }

    [Test]
    public void LiveDistributionAndDetuneEditsGlideConstituentsWithoutResettingPhases()
    {
        FmSynthGraph Graph(FmMultiNode multi) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 150),
            multi,
        ], 2);
        FmMultiNode original = new(2, 1, detune: 0, voiceCount: 2,
            randomizePhase: false, gainCompensation: false);
        FmMultiNode harmonic = new(2, 1,
            distribution: FmMultiDistribution.Harmonic,
            harmonicComponents: "1 2", randomizePhase: true,
            gainCompensation: false, cycleLength: 300);
        FmSynthSound old = new(Graph(original), new EmptyEnvelopes());
        FmSynthSound control = new(Graph(original), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        SoundState comparison = control.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 117, new float[117]);
        control.Render(context, comparison, 0, 117, new float[117]);

        FmSynthSound next = new(Graph(harmonic), new EmptyEnvelopes());
        old.CanAdoptRunningState(next, voice).Should().BeTrue();
        old.TryPrepareRunningState(next, voice).Should().BeTrue();
        old.CommitPreparedRunningState(next, voice);
        float[] edited = new float[300], reference = new float[300];
        next.Render(context, voice, 117, 300, edited);
        control.Render(context, comparison, 117, 300, reference);
        edited[0].Should().BeApproximately(reference[0], 1e-5f,
            "the first sample after publication must continue both old phases");
        edited.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(130, 170)
            .Max(i => Math.Abs(edited[i] - reference[i]))
            .Should().BeGreaterThan(0.2f,
                "the second constituent must take its new harmonic pitch");
    }

    [Test]
    public void LiveMultiRandomPhaseAndCycleChangesKeepOldOffsetsAndAffectNewNotes()
    {
        FmSynthGraph Graph(bool random, double cycle) => new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, detune: 0, voiceCount: 3,
                randomizePhase: random, cycleLength: cycle,
                gainCompensation: true),
        ], 2);
        FmSynthSound old = new(Graph(false, 100), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 140, new float[140]);
        FmSynthSound updated = new(Graph(true, 1024), new EmptyEnvelopes());
        old.TryPrepareRunningState(updated, voice).Should().BeTrue();
        old.CommitPreparedRunningState(updated, voice);
        float[] retained = new float[200];
        updated.Render(context, voice, 140, retained.Length, retained);
        retained.Should().OnlyContain(x => Math.Abs(x - 1.0f) < 1e-6f,
            "already-initialized constituents retain their established offsets");
        SoundState fresh = updated.CreateState();
        fresh.SetNoiseSeed(17);
        float[] onset = new float[1];
        updated.Render(context, fresh, 0, 1, onset);
        onset[0].Should().BeLessThan(1.0f,
            "new notes use the revised randomized onset and cycle policy");
    }

    [Test]
    public void RapidSecondMultiEditStartsFromInterpolatedMixInsteadOfJumping()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        RenderContext context = Mono();
        FmSynthSound old = new(Graph(2), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        old.Render(context, voice, 0, 80, new float[80]);
        FmSynthSound expanded = new(Graph(4), new EmptyEnvelopes());
        old.TryPrepareRunningState(expanded, voice).Should().BeTrue();
        old.CommitPreparedRunningState(expanded, voice);
        float[] halfFade = new float[32];
        expanded.Render(context, voice, 80, halfFade.Length, halfFade);

        FmSynthSound reduced = new(Graph(1), new EmptyEnvelopes());
        expanded.TryPrepareRunningState(reduced, voice).Should().BeTrue();
        expanded.CommitPreparedRunningState(reduced, voice);
        float[] second = new float[96];
        reduced.Render(context, voice, 112, second.Length, second);
        // At the edit boundary all four constituents start their new
        // transitions from their currently interpolated gains.
        // Frame 111 was just before the second edit, frame 112 is the
        // next sample. At 32/64 the old blend is exactly halfway.
        second[0].Should().BeApproximately(0.75f, 1e-6f,
            "the second edit must begin from the interpolated current mix");
        Math.Abs(second[0] - halfFade[31]).Should().BeLessThan(0.02f,
            "a rapid edit must not cause an audible gain discontinuity");
        second[80].Should().BeApproximately(0.25f, 1e-6f);
    }

    [Test]
    public void NestedMultiCanRetireAndReviveConstituentsWithoutRestartingParent()
    {
        FmSynthGraph Graph(int innerCount) => new([
            new FmConstantNode(1, 0.1),
            new FmMultiNode(2, 1, voiceCount: innerCount, detune: 0,
                gainCompensation: false, randomizePhase: false),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                gainCompensation: false, randomizePhase: false),
        ], 3);
        FmSynthSound old = new(Graph(3), new EmptyEnvelopes());
        SoundState state = old.CreateState();
        RenderContext context = Mono();
        float[] initial = new float[80];
        old.Render(context, state, 0, 80, initial);
        initial.Should().OnlyContain(x => Math.Abs(x - 0.6f) < 1e-6f);

        FmSynthSound reduced = new(Graph(1), new EmptyEnvelopes());
        old.TryPrepareRunningState(reduced, state).Should().BeTrue();
        old.CommitPreparedRunningState(reduced, state);
        float[] after = new float[100];
        reduced.Render(context, state, 80, after.Length, after);
        after[0].Should().BeApproximately(0.6f, 1e-6f);
        after[90].Should().BeApproximately(0.2f, 1e-6f,
            "both parent constituents must migrate their inner Multi state");

        FmSynthSound revived = new(Graph(3), new EmptyEnvelopes());
        reduced.TryPrepareRunningState(revived, state).Should().BeTrue();
        reduced.CommitPreparedRunningState(revived, state);
        float[] comeback = new float[110];
        revived.Render(context, state, 180, comeback.Length, comeback);
        comeback[0].Should().BeApproximately(0.2f, 1e-6f);
        comeback[90].Should().BeApproximately(0.6f, 1e-6f);
    }

    [Test]
    public void UnpreparedMultiGrowthCannotPartiallyModifyTheExistingVoice()
    {
        FmSynthGraph Graph(int count, double gain) => new([
            new FmConstantNode(1, gain),
            new FmMultiNode(2, 1, detune: 0, voiceCount: count,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound old = new(Graph(2, 0.25), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 80, new float[80]);
        FmSynthSound tooLarge = new(Graph(7, 0.8), new EmptyEnvelopes());
        old.CanAdoptRunningState(tooLarge, voice).Should().BeFalse(
            "two existing and four dormant constituents cannot grow to seven");
        old.TryPrepareRunningState(tooLarge, voice).Should().BeFalse();
        float[] unchanged = new float[80];
        old.Render(context, voice, 80, unchanged.Length, unchanged);
        unchanged.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f);
    }

    private static float[] Render(FmSynthGraph graph, int count, ulong seed = 1)
    {
        FmSynthSound sound = new(graph, new EmptyEnvelopes());
        SoundState state = sound.CreateState();
        state.SetNoiseSeed(seed);
        float[] samples = new float[count];
        sound.Render(Mono(), state, 0, count, samples);
        return samples;
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class TenFrameRelease : IEnvelopeCurveResolver, IEnvelopeCurve
    {
        private readonly ObjectId _id;
        public TenFrameRelease(ObjectId id) => _id = id;
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = id == _id ? this : null;
            return curve is not null;
        }
        public double GetValue(long activeFrame, int sampleRate,
            long? noteOffActiveFrame)
            => noteOffActiveFrame.HasValue
                && activeFrame >= noteOffActiveFrame.Value + 10 ? 0 : 1;
        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long noteOffActiveFrame, int sampleRate) => noteOffActiveFrame + 10;
    }

    private sealed class SourceResolver : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly ObjectId _id;
        private readonly ISound _sound;
        public SourceResolver(ObjectId id, ISound sound)
        {
            _id = id; _sound = sound;
        }
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            sound = sourceId == _id ? _sound : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
