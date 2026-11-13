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
public sealed class FmSynthLiveMultiOutputTests
{
    [TestCase(FmOperatorKind.Multiply, 0.85, 0.12)]
    [TestCase(FmOperatorKind.Add, 0.6, -0.3)]
    public void HeldMultiOutputConstantEditCrossfadesWithoutResettingConstituents(
        FmOperatorKind operation, double originalGain, double revisedGain)
    {
        FmSynthSound original = Sound(originalGain, operation);
        FmSynthSound revised = Sound(revisedGain, operation);
        AssertSmoothEdit(original, revised,
            operation == FmOperatorKind.Add);
    }

    [Test]
    public void HeldMultiOutputOperationEditCrossfadesWithoutRephasingConstituents()
    {
        AssertSmoothEdit(
            Sound(0.17, FmOperatorKind.Multiply),
            Sound(0.17, FmOperatorKind.Add), additive: true);
    }

    [Test]
    public void TwoRapidMultiOutputEditsJoinTheLastActuallyAudibleSample()
    {
        FmSynthSound original = Sound(0.90, FmOperatorKind.Multiply);
        FmSynthSound middle = Sound(0.15, FmOperatorKind.Multiply);
        FmSynthSound final = Sound(0.15, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.TryPrepareRunningState(middle, held).Should().BeTrue();
        original.CommitPreparedRunningState(middle, held);
        float[] partial = new float[20];
        middle.Render(context, held, 128, partial.Length, partial);
        middle.TryPrepareRunningState(final, held).Should().BeTrue();
        middle.CommitPreparedRunningState(final, held);

        float[] expected = new float[244], actual = new float[96];
        final.Render(context, final.CreateState(), 0, expected.Length, expected);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        final.Render(context, held, 148, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        AssertInterruptedAffineFade(partial[^1], expected, actual,
            148, 0.90, 0.15, 0.15);
    }

    [Test]
    public void NormalPlaybackMultiOutputEditDoesNotAddAuditionSmoothing()
    {
        FmSynthSound original = Sound(0.85, audition: false);
        FmSynthSound revised = Sound(0.12, audition: false);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], actual = new float[1],
            expected = new float[129];
        original.Render(context, held, 0, before.Length, before);
        revised.Render(context, revised.CreateState(), 0,
            expected.Length, expected);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        revised.Render(context, held, 128, actual.Length, actual);
        actual[0].Should().BeApproximately(expected[128], 3e-6f,
            "ordinary playback must never acquire audition-only crossfades");
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
        FmOperatorKind operation = FmOperatorKind.Multiply,
        bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 390),
            new FmMultiNode(2, 1, voiceCount: 3, detune: 21,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(3, gain),
            new FmOperatorNode(4, operation, [2, 3]),
        ], 4), new EmptyEnvelopes(), enableLiveMultiExpansion: audition);

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
