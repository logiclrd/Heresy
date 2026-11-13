using System;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthLiveCompoundMultiOutputTests
{
    [TestCase(FmOperatorKind.Multiply, 0.8, 0.1)]
    [TestCase(FmOperatorKind.Add, 0.6, -0.2)]
    public void DownstreamConstantEditPreservesIndependentCompoundMultiPhases(
        FmOperatorKind operation, double oldGain, double newGain)
    {
        AssertSmoothEdit(Sound(oldGain, operation),
            Sound(newGain, operation), operation == FmOperatorKind.Add);
    }

    [Test]
    public void DownstreamOperatorChangePreservesCompoundMultiConstituents()
    {
        AssertSmoothEdit(
            Sound(0.3, FmOperatorKind.Multiply),
            Sound(0.3, FmOperatorKind.Add), additive: true);
    }

    [Test]
    public void SecondCompoundMultiOutputEditJoinsPreviouslyBlendedOutput()
    {
        FmSynthSound first = Sound(0.85, FmOperatorKind.Multiply);
        FmSynthSound second = Sound(0.1, FmOperatorKind.Multiply);
        FmSynthSound third = Sound(0.1, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        first.Render(context, held, 0, 128, new float[128]);
        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] partial = new float[20];
        second.Render(context, held, 128, partial.Length, partial);

        second.TryPrepareRunningState(third, held).Should().BeTrue();
        second.CommitPreparedRunningState(third, held);
        float[] expected = new float[244], output = new float[96];
        third.Render(context, third.CreateState(), 0, expected.Length, expected);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        third.Render(context, held, 148, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "neither the compound Multi's children nor the fade may allocate in PCM");
        AssertInterruptedAffineFade(partial[^1], expected, output,
            148, 0.85, 0.1, 0.0);
    }

    [Test]
    public void NonAuditionCompoundMultiOutputGainRemainsImmediate()
    {
        FmSynthSound before = Sound(0.8, audition: false);
        FmSynthSound after = Sound(0.1, audition: false);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        float[] refFrames = new float[129], actual = new float[1];
        after.Render(context, after.CreateState(), 0, refFrames.Length, refFrames);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        after.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(refFrames[128], 3e-6f,
            "ordinary pattern and export rendering must not acquire audition fades");
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
                    + newReference[start + i] * w), 3e-6f,
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

    private static FmSynthSound Sound(double gain,
        FmOperatorKind mode = FmOperatorKind.Multiply,
        bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 390,
                curvature: 1.4),
            new FmOscillatorNode(2, FmOscillatorWaveform.Triangle, 515,
                minimum: -0.5, maximum: 0.8),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            new FmMultiNode(4, 3, voiceCount: 3, detune: 21,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(5, gain),
            new FmOperatorNode(6, mode, [4, 5]),
        ], 6), new NoEnvelopes(), enableLiveMultiExpansion: audition);

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class NoEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
