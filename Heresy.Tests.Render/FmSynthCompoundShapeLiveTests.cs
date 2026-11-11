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
        // Current frame samples depend on the phase accumulated *before*
        // the retune, not the new frequency which advances it afterwards.
        SoundState referenceState = original.CreateState();
        original.Render(context, referenceState, 0, 128, new float[128]);
        original.Render(context, referenceState, 128, 1, expected);

        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        revised.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[0], 2e-6f,
            "frequency-only changes must not freeze an Operator output");
    }

    [Test]
    public void OrdinaryCompoundPlaybackDoesNotAddAnAuditionOnlyShapeFade()
    {
        FmSynthSound oldSound = Sound(FmOperatorKind.Add,
            FmOscillatorWaveform.Sine, audition: false);
        FmSynthSound next = Sound(FmOperatorKind.Add,
            FmOscillatorWaveform.Square, audition: false);
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        float[] previous = new float[128], expected = new float[1],
            actual = new float[1];
        oldSound.Render(context, held, 0, previous.Length, previous);
        SoundState fresh = next.CreateState();
        next.Render(context, fresh, 0, 128, new float[128]);
        next.Render(context, fresh, 128, 1, expected);

        oldSound.TryPrepareRunningState(next, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(next, held);
        next.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[0], 2e-6f,
            "ordinary playback must not inherit audition-only smoothing");
        Math.Abs(actual[0] - previous[^1]).Should().BeGreaterThan(0.1f,
            "the regular output should not be held at the prior sample");
    }


    [Test]
    public void HeldConstantGainEditCrossfadesTheAudibleStatelessGraph()
    {
        FmSynthSound SoundWithGain(double gain, bool audition = true)
            => new(new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 400),
                new FmConstantNode(2, gain),
                new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
            ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: audition);
        FmSynthSound beforeSound = SoundWithGain(0.9);
        FmSynthSound afterSound = SoundWithGain(0.1);
        AssertSmoothStatelessEdit(beforeSound, afterSound);
    }

    [Test]
    public void HeldOperatorKindEditCrossfadesTheAudibleStatelessGraph()
    {
        FmSynthSound beforeSound = Sound(FmOperatorKind.Add,
            FmOscillatorWaveform.Sine);
        FmSynthSound afterSound = Sound(FmOperatorKind.Multiply,
            FmOscillatorWaveform.Sine);
        AssertSmoothStatelessEdit(beforeSound, afterSound);
    }

    [Test]
    public void HeldConstantOnlyOutputCrossfadesOnValueEdit()
    {
        FmSynthSound SoundWithValue(double value)
            => new(new FmSynthGraph([
                new FmConstantNode(1, value),
            ], 1), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        AssertSmoothStatelessEdit(SoundWithValue(0.9), SoundWithValue(-0.4));
    }

    [Test]
    public void SecondStatelessParameterEditStartsAtTheCurrentBlendedOutput()
    {
        FmSynthSound SoundWith(double gain, FmOperatorKind operation)
            => new(new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 400),
                new FmConstantNode(2, gain),
                new FmOperatorNode(3, operation, [1, 2]),
            ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        FmSynthSound initial = SoundWith(0.9, FmOperatorKind.Multiply);
        FmSynthSound first = SoundWith(0.1, FmOperatorKind.Multiply);
        FmSynthSound second = SoundWith(0.1, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = initial.CreateState();
        initial.Render(context, held, 0, 128, new float[128]);
        initial.TryPrepareRunningState(first, held).Should().BeTrue();
        initial.CommitPreparedRunningState(first, held);
        float[] partial = new float[20];
        first.Render(context, held, 128, partial.Length, partial);

        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] actual = new float[96], reference = new float[244];
        second.Render(context, second.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        second.Render(context, held, 148, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        actual[0].Should().Be(partial[^1]);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp(i / 64.0, 0, 1);
            double w = t * t * (3 - 2 * t);
            actual[i].Should().BeApproximately(
                (float)(partial[^1] * (1 - w) + reference[148 + i] * w),
                2e-6f);
        }
    }

    [Test]
    public void ControlOnlyConstantEditDoesNotFreezeOscillatorFrequencyModulation()
    {
        FmSynthSound SoundWithMultiplier(double scalar) => new(
            new FmSynthGraph([
                new FmConstantNode(1, scalar),
                new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 100,
                    multiplierNodeId: 1),
            ], 2), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        FmSynthSound before = SoundWithMultiplier(1.0);
        FmSynthSound after = SoundWithMultiplier(3.0);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        float[] actual = new float[2];
        after.Render(context, held, 128, actual.Length, actual);
        actual[0].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * 100 * 128 / 48000), 2e-6f);
        actual[1].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * (100 * 128 + 300) / 48000), 2e-6f,
            "a control-only Constant edit must retune continuously, not hold PCM");
    }

    [Test]
    public void OperatorUsedOnlyForPitchModulationDoesNotCrossfadeTheAudibleOutput()
    {
        FmSynthSound SoundWithControl(FmOperatorKind operation)
            => new(new FmSynthGraph([
                new FmConstantNode(1, 1),
                new FmConstantNode(2, 2),
                new FmOperatorNode(3, operation, [1, 2]),
                new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 100,
                    multiplierNodeId: 3),
            ], 4), new EmptyEnvelopes(), enableLiveMultiExpansion: true);

        FmSynthSound before = SoundWithControl(FmOperatorKind.Add);
        FmSynthSound after = SoundWithControl(FmOperatorKind.Multiply);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        float[] actual = new float[2];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);

        actual[0].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * (300 * 128.0) / 48000), 2e-6f);
        actual[1].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * (300 * 128.0 + 200) / 48000),
            2e-6f, "the changed pitch-control Operator must not start an"
                + " unnecessary audible-output transition");
    }

    [Test]
    public void NonAuditionStatelessConstantAndOperatorEditsRemainImmediate()
    {
        FmSynthSound Build(double gain, FmOperatorKind operation)
            => new(new FmSynthGraph([
                new FmConstantNode(1, gain),
                new FmConstantNode(2, 0.4),
                new FmOperatorNode(3, operation, [1, 2]),
            ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: false);
        FmSynthSound original = Build(0.8, FmOperatorKind.Add);
        FmSynthSound revised = Build(-0.2, FmOperatorKind.Multiply);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] output = new float[1];
        revised.Render(context, held, 128, 1, output);
        output[0].Should().BeApproximately(-0.08f, 2e-6f);
    }

    private static void AssertSmoothStatelessEdit(
        FmSynthSound original, FmSynthSound revised)
    {
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] previous = new float[128], actual = new float[128],
            revisedReference = new float[256];
        original.Render(context, held, 0, previous.Length, previous);
        revised.Render(context, revised.CreateState(), 0,
            revisedReference.Length, revisedReference);

        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "bounded stateless control transitions must not allocate in PCM");

        actual[0].Should().Be(previous[^1]);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp(i / 64.0, 0, 1);
            double w = t * t * (3 - 2 * t);
            float expected = (float)(previous[^1] * (1 - w)
                + revisedReference[128 + i] * w);
            actual[i].Should().BeApproximately(expected, 2e-6f,
                "graph-level scalar and Operator edits must join the last output"
                + " without altering existing oscillator phase");
        }
    }

    private static FmSynthSound Sound(
        FmOperatorKind operation,
        FmOscillatorWaveform firstWaveform,
        double curvature = 0.0, double minimum = -1.0,
        double maximum = 1.0, double firstHz = 400.0,
        bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, firstWaveform, firstHz,
                minimum: minimum, maximum: maximum, curvature: curvature),
            new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 233.0),
            new FmOperatorNode(3, operation, [1, 2]),
        ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: audition);

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
