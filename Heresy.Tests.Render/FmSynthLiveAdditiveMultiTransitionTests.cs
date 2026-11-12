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
public sealed class FmSynthLiveAdditiveMultiTransitionTests
{
    private const int SmoothFrames = 4800; // 100 ms at the 48 kHz fixture rate
    private static readonly ObjectId SampleId = (ObjectId)9473U;

    [Test]
    public void LargeAdditiveConstantStepKeepsFilteredMultiWaveformMoving()
    {
        SampleSound sample = Sample();
        FilterInstance filter = OnePole();
        FmSynthSound original = Sound(sample, filter, 0.9, FmOperatorKind.Add);
        FmSynthSound edited = Sound(sample, filter, -0.8, FmOperatorKind.Add);
        AssertMovingAffineBlend(original, edited);
    }

    [TestCase(FmOperatorKind.Add, FmOperatorKind.Multiply)]
    [TestCase(FmOperatorKind.Multiply, FmOperatorKind.Add)]
    public void SwitchingAddAndMultiplyMorphsLiveGainAndOffsetWithoutFreezingSignal(
        FmOperatorKind originalMode, FmOperatorKind revisedMode)
    {
        SampleSound sample = Sample();
        FilterInstance filter = OnePole();
        AssertMovingAffineBlend(
            Sound(sample, filter, 0.8, originalMode),
            Sound(sample, filter, 0.8, revisedMode));
    }

    [Test]
    public void SecondAdditiveEditStartsFromCurrentMovingGainAndOffset()
    {
        SampleSound sample = Sample();
        FilterInstance filter = OnePole();
        FmSynthSound first = Sound(sample, filter, 0.8, FmOperatorKind.Add);
        FmSynthSound middle = Sound(sample, filter, 0.1, FmOperatorKind.Add);
        FmSynthSound last = Sound(sample, filter, -0.3, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState live = first.CreateState();
        first.Render(context, live, 0, 128, new float[128]);

        first.TryPrepareRunningState(middle, live).Should().BeTrue();
        first.CommitPreparedRunningState(middle, live);
        middle.Render(context, live, 128, 73, new float[73]);

        middle.TryPrepareRunningState(last, live).Should().BeTrue();
        middle.CommitPreparedRunningState(last, live);
        float[] reference = new float[201 + SmoothFrames + 32];
        last.Render(context, last.CreateState(), 0, reference.Length, reference);
        float[] actual = new float[SmoothFrames + 32];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        last.Render(context, live, 201, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        double oldOffset = Mix(0.8, 0.1, Weight(72));
        for (int i = 0; i < actual.Length; i++)
        {
            double w = Weight(i);
            double signal = reference[201 + i] + 0.3;
            double expected = signal + Mix(oldOffset, -0.3, w);
            actual[i].Should().BeApproximately((float)expected, 5e-6f,
                "overlapping additive changes must keep the moving Multi"
                + " signal and the previous audible offset without restarting"
                + " any bound Filter or Sample");
        }
    }

    // Physical slice-60 feedback: 512 moving frames are mathematically
    // continuous but still audibly pop when a large Add DC offset changes.
    // And Multiply-only edits still take the old 64-frame waveform HOLD,
    // causing a smaller click instead of an advancing-signal gain ramp.
    // Assert a 100 ms *moving* affine transition at BOTH output rates.
    [TestCase(48000, FmOperatorKind.Add, FmOperatorKind.Add, 0.9, -0.8)]
    [TestCase(48000, FmOperatorKind.Add, FmOperatorKind.Multiply, 0.8, 0.8)]
    [TestCase(48000, FmOperatorKind.Multiply, FmOperatorKind.Add, 0.8, 0.8)]
    [TestCase(48000, FmOperatorKind.Multiply, FmOperatorKind.Multiply, 0.8, 0.1)]
    [TestCase(96000, FmOperatorKind.Add, FmOperatorKind.Multiply, 0.8, 0.8)]
    [TestCase(96000, FmOperatorKind.Multiply, FmOperatorKind.Multiply, 0.8, 0.1)]
    public void AudibleOutputMappingSlewsWithMovingMultiForAtLeastOneTenthSecond(
        int sampleRate, FmOperatorKind beforeMode, FmOperatorKind afterMode,
        double beforeConstant, double afterConstant)
    {
        SampleSound sample = Sample();
        FilterInstance filter = OnePole();
        FmSynthSound before = Sound(sample, filter, beforeConstant, beforeMode);
        FmSynthSound after = Sound(sample, filter, afterConstant, afterMode);
        RenderContext context = Mono(sampleRate);
        int ramp = sampleRate / 10;
        int length = 128 + ramp + 48;

        SoundState held = before.CreateState();
        float[] oldReference = new float[length];
        float[] newReference = new float[length];
        before.Render(context, before.CreateState(), 0, length, oldReference);
        after.Render(context, after.CreateState(), 0, length, newReference);
        before.Render(context, held, 0, 128, new float[128]);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        float[] actual = new float[ramp + 48];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);

        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / ramp, 0, 1);
            double w = t * t * (3 - 2 * t);
            double expected = oldReference[128 + i] * (1 - w)
                + newReference[128 + i] * w;
            actual[i].Should().BeApproximately((float)expected, 6e-6f,
                "even Multiply-only changes must continue the original"
                + " Sample/Filter/Multi waveform while gain/offset ramps"
                + " for a full 100 ms at the active PCM sample rate");
        }
    }

    [Test]
    public void OrdinaryPlaybackHasNoAdditiveMorph()
    {
        SampleSound sample = Sample();
        FilterInstance filter = OnePole();
        FmSynthSound first = Sound(sample, filter, 0.9,
            FmOperatorKind.Add, audition: false);
        FmSynthSound next = Sound(sample, filter, -0.8,
            FmOperatorKind.Add, audition: false);
        RenderContext context = Mono();
        SoundState state = first.CreateState();
        first.Render(context, state, 0, 128, new float[128]);
        float[] expected = new float[129];
        next.Render(context, next.CreateState(), 0, expected.Length, expected);
        first.TryPrepareRunningState(next, state).Should().BeTrue();
        first.CommitPreparedRunningState(next, state);
        float[] actual = new float[1];
        next.Render(context, state, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[128], 5e-6f);
    }

    private static void AssertMovingAffineBlend(FmSynthSound oldSound,
        FmSynthSound revisedSound)
    {
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        float[] originalReference = new float[128 + SmoothFrames + 32];
        float[] newReference = new float[originalReference.Length];
        oldSound.Render(context, oldSound.CreateState(), 0,
            originalReference.Length, originalReference);
        revisedSound.Render(context, revisedSound.CreateState(), 0,
            newReference.Length, newReference);
        oldSound.Render(context, held, 0, 128, new float[128]);

        oldSound.TryPrepareRunningState(revisedSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(revisedSound, held);
        float[] actual = new float[SmoothFrames + 32];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revisedSound.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "a longer additive output transition cannot allocate or"
            + " recreate any Sample, Filter or Multi DSP state in PCM");
        for (int i = 0; i < actual.Length; i++)
        {
            double weight = Weight(i);
            double expected = Mix(originalReference[128 + i],
                newReference[128 + i], weight);
            actual[i].Should().BeApproximately((float)expected, 5e-6f,
                "the original and revised gain/offset are applied to the"
                + " SAME advancing Sample/Filter/Multi signal at each frame,"
                + " without holding the old waveform or abruptly stepping DC");
        }
    }

    private static double Weight(int frame)
    {
        double t = Math.Clamp((double)frame / SmoothFrames, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Mix(double old, double next, double w)
        => old * (1 - w) + next * w;

    private static FmSynthSound Sound(SampleSound sample, FilterInstance filter,
        double value, FmOperatorKind mode, bool audition = true)
    {
        SampleResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.002),
            new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 390),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
            new FmMultiNode(5, 4, voiceCount: 3, detune: 17,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(6, value),
            new FmOperatorNode(7, mode, [5, 6]),
        ], 7), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static FilterInstance OnePole()
    {
        FilterDefinition definition = new((ObjectId)9474U, "Additive pole");
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        return filter;
    }

    private static SampleSound Sample()
    {
        float[] pcm = Enumerable.Range(0, 2400)
            .Select(i => (float)(0.31 + 0.15 * Math.Sin(i * 0.097)
                + 0.07 * Math.Cos(i * 0.043))).ToArray();
        return new(new SampleDefinition(SampleId, "Additive Sample",
            new ExternalAssetReference("additive-sample.wav")),
            new MemorySampleData(48000, 1, pcm));
    }

    private static RenderContext Mono(int sampleRate = 48000) => new(new RenderConfiguration(sampleRate,
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
