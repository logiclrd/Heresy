using System;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
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
public sealed class FmSynthLiveFilteredMultiOutputTests
{
    private static readonly ObjectId SampleId = (ObjectId)9470U;

    [TestCase(FmOperatorKind.Multiply, 0.87, 0.16)]
    [TestCase(FmOperatorKind.Add, 0.67, -0.21)]
    public void FrozenFilteredSampleMultiChildFadesOuterGainWithoutReinitializingHistory(
        FmOperatorKind operation, double beforeGain, double afterGain)
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        AssertSmoothEdit(Sound(sample, filter, beforeGain, operation),
            Sound(sample, filter, afterGain, operation),
            operation == FmOperatorKind.Add);
    }

    [Test]
    public void FrozenFilteredSampleMultiChildFadesOuterOperatorMode()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        AssertSmoothEdit(Sound(sample, filter, 0.31, FmOperatorKind.Multiply),
            Sound(sample, filter, 0.31, FmOperatorKind.Add), additive: true);
    }

    [Test]
    public void RepeatedOuterEditsAnchorAtBlendedOutputWithFilterHistoryContinuing()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        FmSynthSound first = Sound(sample, filter, 0.83);
        FmSynthSound second = Sound(sample, filter, 0.17);
        FmSynthSound third = Sound(sample, filter, 0.17, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        first.Render(context, held, 0, 128, new float[128]);

        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] partial = new float[19];
        second.Render(context, held, 128, partial.Length, partial);

        second.TryPrepareRunningState(third, held).Should().BeTrue();
        second.CommitPreparedRunningState(third, held);
        float[] reference = new float[243], output = new float[96];
        third.Render(context, third.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        third.Render(context, held, 147, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "neither Sample/Filter history nor Multi children may allocate in PCM");
        AssertInterruptedAffineFade(partial[^1], reference, output,
            147, 0.83, 0.17, 0.17);
    }

    [Test]
    public void FilterTailAndOriginalSampleEofSurviveParentFade()
    {
        SampleSound sample = Sample(400);
        FilterInstance filter = OnePole();
        FmSynthSound before = Sound(sample, filter, 0.85);
        FmSynthSound after = Sound(sample, filter, 0.19);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);

        // One branch ends at the original Sample EOF, the serial
        // OnePole tail decays, and the parallel Oscillator continues.
        // All three independent Multi constituent histories must match
        // a fresh reference beyond the outer 100 ms moving-affine transition.
        float[] reference = new float[128 + AdditiveFadeFrames + 64],
            actual = new float[AdditiveFadeFrames + 64];
        after.Render(context, after.CreateState(), 0,
            reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        for (int i = AdditiveFadeFrames; i < actual.Length; i++)
            actual[i].Should().BeApproximately(reference[128 + i], 4e-6f,
                "the unchanged Sample readhead and Filter tail must never"
                + " be restarted, trimmed or re-filtered by an outer gain edit");
    }

    [Test]
    public void NormalPlaybackKeepsImmediateFilteredMultiOutputControls()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        FmSynthSound before = Sound(sample, filter, 0.85, audition: false);
        FmSynthSound after = Sound(sample, filter, 0.15, audition: false);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        float[] reference = new float[129], actual = new float[1];
        after.Render(context, after.CreateState(), 0, reference.Length, reference);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        after.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(reference[128], 4e-6f,
            "ordinary playback/export must not get audition-only fades");
    }

    private const int AdditiveFadeFrames = 4800;

    private static void AssertSmoothEdit(FmSynthSound before,
        FmSynthSound after, bool additive = false)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        SoundState fresh = after.CreateState();
        int length = AdditiveFadeFrames + 32;
        float[] previous = new float[128], actual = new float[length],
            reference = new float[128 + length];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, fresh, 0, reference.Length, reference);
        float[] oldReference = new float[128 + length];
        SoundState previousState = before.CreateState();
        before.Render(context, previousState, 0,
            oldReference.Length, oldReference);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "the already-bound Multi children and histories must not"
            + " allocate or be reconstructed in PCM");
        AssertMovingAffineFade(oldReference, reference, actual, 128);
    }

    private static void AssertMovingAffineFade(float[] oldReference,
        float[] newReference, float[] actual, int start)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / AdditiveFadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            actual[i].Should().BeApproximately(
                (float)(oldReference[start + i] * (1 - w)
                    + newReference[start + i] * w), 4e-6f,
                "Add changes must retain the moving original signal while"
                + " smoothing its DC offset and gain over 100 ms");
        }
    }

    private static void AssertInterruptedAffineFade(float lastAudible,
        float[] finalReference, float[] actual, int start,
        double firstScale, double middleScale, double revisedAddConstant)
    {
        // An interrupted moving Multiply ramp becomes the next ramp's
        // starting gain: don't hold an audio sample or reset DSP history.
        double before = Math.Clamp((double)(start - 1 - 128)
            / AdditiveFadeFrames, 0, 1);
        double previousWeight = before * before * (3 - 2 * before);
        double previousScale = firstScale * (1 - previousWeight)
            + middleScale * previousWeight;
        double previousSignal = finalReference[start - 1] - revisedAddConstant;
        lastAudible.Should().BeApproximately(
            (float)(previousScale * previousSignal), 6e-6f);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / AdditiveFadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            double movingSignal = finalReference[start + i] - revisedAddConstant;
            double expected = movingSignal * (previousScale * (1 - w) + w)
                + revisedAddConstant * w;
            actual[i].Should().BeApproximately((float)expected, 6e-6f,
                "the second edit must inherit the previous moving gain"
                + " and continue original Multi audio without a hold");
        }
    }

    private static FmSynthSound Sound(SampleSound sample, FilterInstance filter,
        double gain, FmOperatorKind operation = FmOperatorKind.Multiply,
        bool audition = true)
    {
        SampleResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.002),
            new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 390),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
            new FmMultiNode(5, 4, voiceCount: 3, detune: 17,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(6, gain),
            new FmOperatorNode(7, operation, [5, 6]),
        ], 7), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static FilterInstance OnePole()
    {
        FilterDefinition definition = new((ObjectId)9471U, "Multi Sample pole");
        FilterInstance instance = new(definition);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        return instance;
    }

    private static SampleSound Sample(int frames)
    {
        float[] pcm = Enumerable.Range(0, frames)
            .Select(i => (float)(0.31 + 0.15 * Math.Sin(i * 0.097)
                + 0.07 * Math.Cos(i * 0.043))).ToArray();
        return new(new SampleDefinition(SampleId,
            "Slice 60 Filter Sample", new ExternalAssetReference("slice60.wav")),
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
