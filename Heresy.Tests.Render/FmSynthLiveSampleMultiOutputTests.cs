using System;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
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
public sealed class FmSynthLiveSampleMultiOutputTests
{
    private static readonly ObjectId SampleId = (ObjectId)9460U;

    [TestCase(FmOperatorKind.Multiply, 0.85, 0.12)]
    [TestCase(FmOperatorKind.Add, 0.55, -0.18)]
    public void FrozenSampleAndOscillatorMultiChildFadesDownstreamGain(
        FmOperatorKind operation, double beforeGain, double afterGain)
    {
        SampleSound sample = Sample(1400);
        AssertSmoothEdit(Sound(sample, beforeGain, operation),
            Sound(sample, afterGain, operation));
    }

    [Test]
    public void FrozenSampleAndOscillatorMultiChildFadesDownstreamOperation()
    {
        SampleSound sample = Sample(1400);
        AssertSmoothEdit(Sound(sample, 0.35, FmOperatorKind.Multiply),
            Sound(sample, 0.35, FmOperatorKind.Add));
    }

    [Test]
    public void RepeatedDownstreamEditsJoinBlendedOutputWithoutRewindingSamples()
    {
        SampleSound sample = Sample(1400);
        FmSynthSound first = Sound(sample, 0.85);
        FmSynthSound second = Sound(sample, 0.15);
        FmSynthSound third = Sound(sample, 0.15, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        first.Render(context, held, 0, 128, new float[128]);
        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] partiallyFaded = new float[18];
        second.Render(context, held, 128, partiallyFaded.Length, partiallyFaded);

        second.TryPrepareRunningState(third, held).Should().BeTrue();
        second.CommitPreparedRunningState(third, held);
        float[] reference = new float[242], output = new float[96];
        third.Render(context, third.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        third.Render(context, held, 146, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        AssertFade(partiallyFaded[^1], reference, output, 146);
    }

    [Test]
    public void OriginalSampleEofSurvivesOuterFadeWithOscillatorStillSounding()
    {
        SampleSound sample = Sample(400);
        FmSynthSound previous = Sound(sample, 0.8);
        FmSynthSound revised = Sound(sample, 0.2);
        RenderContext context = Mono();
        SoundState held = previous.CreateState();
        previous.Render(context, held, 0, 128, new float[128]);
        previous.TryPrepareRunningState(revised, held).Should().BeTrue();
        previous.CommitPreparedRunningState(revised, held);
        float[] reference = new float[768], output = new float[640];
        revised.Render(context, revised.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        // Beyond the 64-frame output fade, every constituent's original
        // fractional Sample playhead reaches EOF in the same way as a new
        // invocation. The parallel Oscillator still has audible output.
        for (int i = 64; i < output.Length; i++)
            output[i].Should().BeApproximately(reference[128 + i], 4e-6f);
    }

    [Test]
    public void NormalPlaybackDoesNotFadeFrozenSampleMultiOutputControls()
    {
        SampleSound sample = Sample(1000);
        FmSynthSound previous = Sound(sample, 0.8, audition: false);
        FmSynthSound revised = Sound(sample, 0.1, audition: false);
        RenderContext context = Mono();
        SoundState held = previous.CreateState();
        previous.Render(context, held, 0, 128, new float[128]);
        float[] expected = new float[129], actual = new float[1];
        revised.Render(context, revised.CreateState(), 0, expected.Length, expected);
        previous.TryPrepareRunningState(revised, held).Should().BeTrue();
        previous.CommitPreparedRunningState(revised, held);
        revised.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[128], 4e-6f);
    }

    private static void AssertSmoothEdit(FmSynthSound before, FmSynthSound after)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        float[] previous = new float[128], reference = new float[288],
            output = new float[160];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, after.CreateState(), 0, reference.Length, reference);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "frozen Multi Sample readheads and oscillator phases cannot"
            + " be reconstructed or allocated in PCM");
        AssertFade(previous[^1], reference, output, 128);
    }

    private static void AssertFade(float lastAudible,
        float[] reference, float[] output, int start)
    {
        output[0].Should().Be(lastAudible);
        for (int i = 0; i < output.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3.0 - 2.0 * t);
            output[i].Should().BeApproximately(
                (float)(lastAudible * (1 - w) + reference[start + i] * w),
                4e-6f,
                "all Multi constituents preserve their actual Sample"
                + " readheads and independent oscillator phases");
        }
    }

    private static FmSynthSound Sound(SampleSound sample,
        double gain, FmOperatorKind mode = FmOperatorKind.Multiply,
        bool audition = true)
    {
        SampleResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 390),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            new FmMultiNode(4, 3, voiceCount: 3, detune: 17,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(5, gain),
            new FmOperatorNode(6, mode, [4, 5]),
        ], 6), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static SampleSound Sample(int frames)
    {
        float[] pcm = Enumerable.Range(0, frames)
            .Select(i => (float)(0.32 + 0.16 * Math.Sin(i * 0.097)
                + 0.09 * Math.Cos(i * 0.037))).ToArray();
        return new(new SampleDefinition(SampleId, "Slice 59 frozen Multi Sample",
            new ExternalAssetReference("slice59-sample.wav")),
            new MemorySampleData(48000, 1, pcm));
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class SampleResolver(SampleSound sample)
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
