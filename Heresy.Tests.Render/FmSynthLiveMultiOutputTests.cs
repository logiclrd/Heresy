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
        AssertSmoothEdit(original, revised);
    }

    [Test]
    public void HeldMultiOutputOperationEditCrossfadesWithoutRephasingConstituents()
    {
        AssertSmoothEdit(
            Sound(0.17, FmOperatorKind.Multiply),
            Sound(0.17, FmOperatorKind.Add));
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
        AssertOutputFade(partial[^1], expected, actual, 148);
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

    private static void AssertSmoothEdit(FmSynthSound original,
        FmSynthSound revised)
    {
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], actual = new float[160],
            expected = new float[288];
        original.Render(context, held, 0, before.Length, before);
        revised.Render(context, revised.CreateState(), 0, expected.Length, expected);

        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "the existing Multi constituents must not be rebuilt in PCM");
        AssertOutputFade(before[^1], expected, actual, 128);
    }

    private static void AssertOutputFade(float lastAudible,
        float[] expected, float[] actual, int start)
    {
        actual[0].Should().Be(lastAudible,
            "a downstream gain edit must not click even if Multi has voices");
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3.0 - 2.0 * t);
            actual[i].Should().BeApproximately(
                (float)(lastAudible * (1.0 - w) + expected[start + i] * w),
                3e-6f,
                "every Multi constituent's original phase and ratio must"
                + " survive the final-output transition");
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
