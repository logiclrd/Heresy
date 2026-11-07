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
