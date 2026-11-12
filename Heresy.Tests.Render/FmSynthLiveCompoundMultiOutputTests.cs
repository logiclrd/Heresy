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
            148, 0.1, 0.1);
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

    private const int AdditiveFadeFrames = 512;

    private static void AssertSmoothEdit(FmSynthSound before,
        FmSynthSound after, bool additive = false)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        SoundState fresh = after.CreateState();
        int length = additive ? AdditiveFadeFrames + 32 : 160;
        float[] previous = new float[128], actual = new float[length],
            reference = new float[128 + length];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, fresh, 0, reference.Length, reference);
        float[] oldReference = additive ? new float[128 + length] : [];
        if (additive)
        {
            SoundState previousState = before.CreateState();
            before.Render(context, previousState, 0,
                oldReference.Length, oldReference);
        }

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "the already-bound Multi children and histories must not"
            + " allocate or be reconstructed in PCM");
        if (additive)
            AssertMovingAffineFade(oldReference, reference, actual, 128);
        else
            AssertFade(previous[^1], reference, actual, 128);
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
                + " smoothing its DC offset and gain over 512 frames");
        }
    }

    private static void AssertInterruptedAffineFade(float lastAudible,
        float[] finalReference, float[] actual, int start,
        double previousScale, double revisedAddConstant)
    {
        // The preceding Multiply 64-frame fade may still be underway.
        // Preserve its already audible value as an affine offset around
        // the independently advancing, unchanged Multi signal.
        double previousSignal = finalReference[start - 1] - revisedAddConstant;
        double previousOffset = lastAudible - previousScale * previousSignal;
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / AdditiveFadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            double movingSignal = finalReference[start + i] - revisedAddConstant;
            double expected = movingSignal * (previousScale * (1 - w) + w)
                + previousOffset * (1 - w) + revisedAddConstant * w;
            actual[i].Should().BeApproximately((float)expected, 3e-6f,
                "the second edit must join the previous blend without"
                + " freezing the original Multi's current audio");
        }
    }

    private static void AssertFade(float previous,
        float[] reference, float[] output, int startFrame)
    {
        output[0].Should().Be(previous,
            "a changed output gain/mix must join the audible compound Multi");
        for (int i = 0; i < output.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3.0 - 2.0 * t);
            output[i].Should().BeApproximately(
                (float)(previous * (1.0 - w)
                    + reference[startFrame + i] * w), 3e-6f,
                "both Oscillators in every Multi constituent must preserve"
                + " independent phases and reach the new authored mix");
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
