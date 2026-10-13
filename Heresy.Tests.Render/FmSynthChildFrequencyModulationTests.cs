using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthChildFrequencyModulationTests
{
    private static readonly double C4 = 440.0 * Math.Pow(2, -9.0 / 12.0);
    private static readonly ObjectId SampleId = (ObjectId)100U;

    [TestCase(false)]
    [TestCase(true)]
    public void LinearAndSemitoneInputsIntegrateSamplePosition(bool semitones)
    {
        FmSynthSound sound = MakeSound([
            new FmConstantNode(0, semitones ? 12 : 2),
            new FmSoundSourceNode(1, SampleId, C4, 0,
                exponentialMultiplier: semitones),
        ], 1);
        float[] output = new float[6];
        sound.Render(Mono(16), sound.CreateState(), 0, output.Length, output);
        Assert.That(output, Is.EqualTo(new float[] { 0, 2, 4, 6, 8, 10 }));
    }

    [Test]
    public void AudioRateModulationAccumulatesPositionRatherThanRetuningSameSample()
    {
        FmSynthSound synth = MakeSound([
            new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 4,
                minimum: 1, maximum: 2, constant: true),
            new FmSoundSourceNode(1, SampleId, C4, multiplierNodeId: 0),
        ], 1);
        float[] output = new float[6];
        synth.Render(Mono(16), synth.CreateState(), 0, output.Length, output);
        Assert.That(output, Is.EqualTo(new float[] { 0, 1.5f, 3.5f, 5, 6, 7.5f })
            .Within(1e-5f));
    }

    [Test]
    public void ConstantIgnoresParentPitchAndSpeedButAcceptsConnectedModulation()
    {
        FmSynthSound synth = MakeSound([
            new FmConstantNode(0, 2),
            new FmSoundSourceNode(1, SampleId, C4, 0, constant: true),
        ], 1);
        SoundInvocation high = synth.CreateInvocation(4, 1.25);
        high.State.PitchTrajectory.SetMultiplier(0, 1.5);
        float[] output = new float[5];
        synth.Render(Mono(16), high.State, 0, output.Length, output);
        Assert.That(output, Is.EqualTo(new float[] { 0, 2, 4, 6, 8 }));
    }

    [Test]
    public void NonPositiveLinearModulationMutesAndDoesNotRunPositionBackwards()
    {
        foreach (double gain in new[] { 0.0, -3.0 })
        {
            FmSynthSound synth = MakeSound([
                new FmConstantNode(0, gain),
                new FmSoundSourceNode(1, SampleId, C4, multiplierNodeId: 0),
            ], 1);
            float[] output = new float[8];
            synth.Render(Mono(16), synth.CreateState(), 0, output.Length, output);
            Assert.That(output, Is.All.EqualTo(0));
        }
    }

    [Test]
    public void ModulationMatchesAcrossChunksAndIndependentVoiceStates()
    {
        FmSynthSound sound = MakeSound([
            new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 4,
                minimum: 0.5, maximum: 1.5, constant: true),
            new FmSoundSourceNode(1, SampleId, C4, 0),
        ], 1);
        RenderContext context = Mono(64);
        float[] one = new float[12], split = new float[12];
        SoundState state = sound.CreateState();
        sound.Render(context, state, 0, 12, one);
        SoundState other = sound.CreateState();
        sound.Render(context, other, 0, 5, split.AsSpan(0, 5));
        sound.Render(context, other, 5, 7, split.AsSpan(5, 7));
        Assert.That(split, Is.EqualTo(one));
        Assert.That(() => sound.Render(context, state, 0, 1, new float[1]),
            Throws.TypeOf<NotSupportedException>(),
            "backward replay requires a separate prepared reconstruction boundary");
    }

    [Test]
    public void SteadyModulatedMonoPcmDoesNotAllocatePerFrame()
    {
        FmSynthSound synth = MakeSound([
            new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 21,
                minimum: 0.75, maximum: 1.25, constant: true),
            new FmSoundSourceNode(1, SampleId, C4, 0),
        ], 1);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(48000);
        float[] buffer = new float[128];
        for (int i = 0; i < 4; i++)
            synth.Render(context, state, i * 128, 128, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 4; i < 20; i++)
            synth.Render(context, state, i * 128, 128, buffer);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(bytes, Is.LessThanOrEqualTo(8192),
            "once prepared, no per-sample pitch segment or array allocations are expected");
    }

    private static FmSynthSound MakeSound(FmSynthNode[] nodes, int outputNode)
    {
        SampleDefinition sample = new(SampleId, "Ramp",
            new ExternalAssetReference("ramp.wav"));
        float[] samples = Enumerable.Range(0, 4096).Select(i => (float)i).ToArray();
        SampleSound sound = new(sample, new MemorySampleData(16, 1, samples));
        Resolver resolver = new(sound);
        return new FmSynthSound(new FmSynthGraph(nodes, outputNode),
            resolver, soundResolver: resolver);
    }

    private static RenderContext Mono(int rate) =>
        new(new RenderConfiguration(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private sealed class Resolver(SampleSound sample)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = id == SampleId ? sample : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
