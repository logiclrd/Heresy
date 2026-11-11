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
public sealed class FmSynthCompoundShapeLiveTests
{
    [TestCase(FmOperatorKind.Add, FmOscillatorWaveform.Square, 0.0, -1.0, 1.0)]
    [TestCase(FmOperatorKind.Multiply, FmOscillatorWaveform.Sine, 2.0, -1.0, 1.0)]
    [TestCase(FmOperatorKind.Add, FmOscillatorWaveform.Sine, 0.0, -0.2, 0.4)]
    public void CompoundOscillatorShapeEditCrossfadesTheActualOutputWithoutRephasing(
        FmOperatorKind operation, FmOscillatorWaveform waveform,
        double curvature, double minimum, double maximum)
    {
        // The old one-output-oscillator guard leaves this same inspector
        // shape edit discontinuous when it passes through an Operator.
        FmSynthSound original = Sound(operation, FmOscillatorWaveform.Sine);
        FmSynthSound revised = Sound(operation, waveform, curvature, minimum, maximum);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], actual = new float[128],
            reference = new float[256];
        original.Render(context, held, 0, before.Length, before);
        revised.Render(context, revised.CreateState(), 0, reference.Length, reference);

        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "compound oscillator shape fading must not allocate in PCM");

        actual[0].Should().Be(before[^1],
            "a new shape under an Operator should join the last audible output");
        for (int i = 0; i < 64; i++)
        {
            double t = i / 64.0;
            double weight = t * t * (3.0 - 2.0 * t);
            float expected = (float)(before[^1] * (1.0 - weight)
                + reference[128 + i] * weight);
            actual[i].Should().BeApproximately(expected, 2e-6f);
        }
        for (int i = 64; i < actual.Length; i++)
            actual[i].Should().BeApproximately(reference[128 + i], 2e-6f,
                "the original independent oscillator phases must survive the fade");
    }

    [Test]
    public void SecondCompoundShapeEditJoinsTheAlreadyBlendedOutput()
    {
        FmSynthSound original = Sound(FmOperatorKind.Add, FmOscillatorWaveform.Sine);
        FmSynthSound square = Sound(FmOperatorKind.Add, FmOscillatorWaveform.Square);
        FmSynthSound saw = Sound(FmOperatorKind.Add, FmOscillatorWaveform.Sawtooth);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.TryPrepareRunningState(square, held).Should().BeTrue();
        original.CommitPreparedRunningState(square, held);
        float[] partial = new float[16], after = new float[100],
            reference = new float[244];
        square.Render(context, held, 128, partial.Length, partial);
        saw.Render(context, saw.CreateState(), 0, reference.Length, reference);

        square.TryPrepareRunningState(saw, held).Should().BeTrue();
        square.CommitPreparedRunningState(saw, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        saw.Render(context, held, 144, after.Length, after);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        after[0].Should().Be(partial[^1]);
        for (int i = 0; i < 64; i++)
        {
            double t = i / 64.0;
            double w = t * t * (3.0 - 2.0 * t);
            after[i].Should().BeApproximately(
                (float)(partial[^1] * (1 - w) + reference[144 + i] * w),
                2e-6f);
        }
        for (int i = 64; i < after.Length; i++)
            after[i].Should().BeApproximately(reference[144 + i], 2e-6f);
    }

    [Test]
    public void CompoundFrequencyOnlyEditDoesNotIntroduceAShapeFade()
    {
        FmSynthSound original = Sound(FmOperatorKind.Add,
            FmOscillatorWaveform.Sine, firstHz: 400);
        FmSynthSound revised = Sound(FmOperatorKind.Add,
            FmOscillatorWaveform.Sine, firstHz: 800);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        float[] expected = new float[1], actual = new float[1];
        revised.Render(context, revised.CreateState(), 0, 129, new float[129]);
        // Current frame samples depend on the phase accumulated *before*
        // the retune, not the new frequency which advances it afterwards.
        original.Render(context, original.CreateState(), 0, 129, new float[129]);
        SoundState referenceState = original.CreateState();
        original.Render(context, referenceState, 0, 128, new float[128]);
        original.Render(context, referenceState, 128, 1, expected);

        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        revised.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[0], 2e-6f,
            "frequency-only changes must not freeze an Operator output");
    }

    private static FmSynthSound Sound(
        FmOperatorKind operation,
        FmOscillatorWaveform firstWaveform,
        double curvature = 0.0, double minimum = -1.0,
        double maximum = 1.0, double firstHz = 400.0)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, firstWaveform, firstHz,
                minimum: minimum, maximum: maximum, curvature: curvature),
            new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 233.0),
            new FmOperatorNode(3, operation, [1, 2]),
        ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: true);

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
