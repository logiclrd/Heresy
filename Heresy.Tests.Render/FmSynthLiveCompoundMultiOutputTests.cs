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
        AssertSmoothEdit(Sound(oldGain, operation), Sound(newGain, operation));
    }

    [Test]
    public void DownstreamOperatorChangePreservesCompoundMultiConstituents()
    {
        AssertSmoothEdit(
            Sound(0.3, FmOperatorKind.Multiply),
            Sound(0.3, FmOperatorKind.Add));
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
        AssertFade(partial[^1], expected, output, 148);
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

    private static void AssertSmoothEdit(FmSynthSound before, FmSynthSound after)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        float[] previous = new float[128], output = new float[160],
            reference = new float[288];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, after.CreateState(), 0, reference.Length, reference);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "existing compound Multi child phases must never be rebuilt in PCM");
        AssertFade(previous[^1], reference, output, 128);
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
