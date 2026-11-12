using System;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthLiveDeterministicMultiOutputTests
{
    private static readonly ObjectId EnvelopeId = (ObjectId)9458U;
    private const ulong Seed = 0xBADD1EUL;

    [TestCase(FmOperatorKind.Multiply, 0.8, 0.15)]
    [TestCase(FmOperatorKind.Add, 0.7, -0.25)]
    public void FrozenNoiseAndEnvelopeMultiChildCanFadeDownstreamGain(
        FmOperatorKind operation, double previousGain, double nextGain)
    {
        AssertSmoothEdit(
            Build(previousGain, operation), Build(nextGain, operation),
            operation == FmOperatorKind.Add);
    }

    [Test]
    public void FrozenNoiseAndEnvelopeMultiChildCanFadeDownstreamOperation()
    {
        AssertSmoothEdit(
            Build(0.2, FmOperatorKind.Multiply),
            Build(0.2, FmOperatorKind.Add), additive: true);
    }

    [Test]
    public void RapidDownstreamEditsKeepPreviousBlendedOutputAndChildSeed()
    {
        FmSynthSound first = Build(0.85);
        FmSynthSound second = Build(0.13);
        FmSynthSound last = Build(0.13, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        held.SetNoiseSeed(Seed);
        first.Render(context, held, 0, 128, new float[128]);

        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] previousFade = new float[20];
        second.Render(context, held, 128, previousFade.Length, previousFade);

        second.TryPrepareRunningState(last, held).Should().BeTrue();
        second.CommitPreparedRunningState(last, held);
        SoundState referenceState = last.CreateState();
        referenceState.SetNoiseSeed(Seed);
        float[] reference = new float[244], actual = new float[96];
        last.Render(context, referenceState, 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        last.Render(context, held, 148, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        AssertInterruptedAffineFade(previousFade[^1], reference, actual,
            148, 0.13, 0.13);
    }

    [Test]
    public void FrozenChildEnvelopeReleaseClockSurvivesOuterGainFade()
    {
        FmSynthSound oldSound = Build(0.9);
        FmSynthSound newSound = Build(0.2);
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        SoundState reference = newSound.CreateState();
        held.SetNoiseSeed(Seed);
        reference.SetNoiseSeed(Seed);
        oldSound.Render(context, held, 0, 128, new float[128]);
        newSound.Render(context, reference, 0, 128, new float[128]);

        oldSound.TryPrepareRunningState(newSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(newSound, held);
        newSound.Render(context, held, 128, 128, new float[128]);
        newSound.Render(context, reference, 128, 128, new float[128]);

        TimeSpan off = FrameTime.FrameStartTime(256, 48000);
        held.NoteOffTime = reference.NoteOffTime = off;
        float[] actual = new float[192], expected = new float[192];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        newSound.Render(context, held, 256, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        newSound.Render(context, reference, 256, expected.Length, expected);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[i], 3e-6f,
                "the original constituent Envelope release clock and"
                + " deterministic Noise stream must survive the output edit");
    }

    [Test]
    public void NonAuditionFrozenNoiseAndEnvelopeMultiRemainsImmediate()
    {
        FmSynthSound before = Build(0.9, audition: false);
        FmSynthSound after = Build(0.12, audition: false);
        RenderContext context = Mono();
        SoundState held = before.CreateState(), reference = after.CreateState();
        held.SetNoiseSeed(Seed);
        reference.SetNoiseSeed(Seed);
        before.Render(context, held, 0, 128, new float[128]);
        float[] expected = new float[129], actual = new float[1];
        after.Render(context, reference, 0, expected.Length, expected);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        after.Render(context, held, 128, actual.Length, actual);
        actual[0].Should().BeApproximately(expected[128], 3e-6f,
            "normal playback/export must not gain an audition crossfade");
    }

    private const int AdditiveFadeFrames = 512;

    private static void AssertSmoothEdit(FmSynthSound before,
        FmSynthSound after, bool additive = false)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        SoundState fresh = after.CreateState();
        held.SetNoiseSeed(Seed);
        fresh.SetNoiseSeed(Seed);
        int length = additive ? AdditiveFadeFrames + 32 : 160;
        float[] previous = new float[128], actual = new float[length],
            reference = new float[128 + length];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, fresh, 0, reference.Length, reference);
        float[] oldReference = additive ? new float[128 + length] : [];
        if (additive)
        {
            SoundState previousState = before.CreateState();
            previousState.SetNoiseSeed(Seed);
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

    private static void AssertFade(float lastAudible,
        float[] reference, float[] actual, int start)
    {
        actual[0].Should().Be(lastAudible,
            "downstream controls must join the previous compound Multi output");
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3.0 - 2.0 * t);
            actual[i].Should().BeApproximately(
                (float)(lastAudible * (1.0 - w)
                    + reference[start + i] * w), 3e-6f,
                "each constituent keeps its original Oscillator phase,"
                + " Noise seed and frozen Envelope time");
        }
    }

    private static FmSynthSound Build(double gain,
        FmOperatorKind mode = FmOperatorKind.Multiply,
        bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 440,
                curvature: 1.2),
            new FmNoiseNode(2),
            new FmEnvelopeNode(3, EnvelopeId),
            new FmConstantNode(4, 0.15),
            new FmOperatorNode(5, FmOperatorKind.Multiply, [2, 4]),
            new FmOperatorNode(6, FmOperatorKind.Multiply, [1, 3]),
            new FmOperatorNode(7, FmOperatorKind.Add, [5, 6]),
            new FmMultiNode(8, 7, voiceCount: 3, detune: 17,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(9, gain),
            new FmOperatorNode(10, mode, [8, 9]),
        ], 10), new OneEnvelope(EnvelopeId, FrozenCurve()),
            enableLiveMultiExpansion: audition);

    private static NodeEnvelopeCurve FrozenCurve()
    {
        NodeEnvelopeDefinition definition = new(EnvelopeId, "Frozen Multi gain");
        definition.ReplaceCurve([
            new(TimeSpan.Zero, 0.25),
            new(TimeSpan.FromMilliseconds(1), 0.4),
            new(TimeSpan.FromMilliseconds(3), 0.4),
            new(TimeSpan.FromMilliseconds(6), 0),
        ], 2, 2, EnvelopeSustainLoopMode.Forward);
        return new(definition);
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class OneEnvelope(ObjectId id, NodeEnvelopeCurve frozen)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = requested == id ? frozen : null;
            return curve is not null;
        }
    }
}
