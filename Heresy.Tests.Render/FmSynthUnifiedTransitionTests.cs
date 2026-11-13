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

/// <summary>Live editing should have one sample-rate-scaled smoothstep
/// clock. Frequency steps interpolate in log2(hertz), never linear Hz.</summary>
[TestFixture]
public sealed class FmSynthUnifiedTransitionTests
{
    private const int TransitionMilliseconds = 100;

    [TestCase(48000, 220.0, 440.0)]
    [TestCase(96000, 440.0, 1760.0)]
    public void OscillatorFrequencyMigratesGeometricallyWithoutRephasing(
        int sampleRate, double firstHz, double nextHz)
    {
        FmSynthSound previous = Oscillator(firstHz);
        FmSynthSound next = Oscillator(nextHz);
        RenderContext context = Mono(sampleRate);
        SoundState ongoing = previous.CreateState();
        float[] warmup = new float[128];
        previous.Render(context, ongoing, 0, warmup.Length, warmup);
        previous.TryPrepareRunningState(next, ongoing).Should().BeTrue();
        previous.CommitPreparedRunningState(next, ongoing);

        int duration = sampleRate * TransitionMilliseconds / 1000;
        float[] actual = new float[duration + 32];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        next.Render(context, ongoing, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);

        double phase = (firstHz * 128 / sampleRate) % 1.0;
        for (int i = 0; i < actual.Length; i++)
        {
            double expected = Math.Sin(2 * Math.PI * phase);
            actual[i].Should().BeApproximately((float)expected, 5e-5f,
                "the phase integral must use the logarithmic 100 ms"
                + " frequency trajectory, not an abrupt frequency edit");
            double t = Math.Clamp((double)i / duration, 0, 1);
            double weight = t * t * (3 - 2 * t);
            double hz = firstHz * Math.Pow(nextHz / firstHz, weight);
            phase = (phase + hz / sampleRate) % 1.0;
        }
    }

    [Test]
    public void GenericWaveformTransitionRunsForTheFullStandardClock()
    {
        FmSynthSound previous = Oscillator(390, FmOscillatorWaveform.Sine);
        FmSynthSound next = Oscillator(390, FmOscillatorWaveform.Square);
        RenderContext context = Mono(48000);
        SoundState ongoing = previous.CreateState();
        float[] warmup = new float[128];
        previous.Render(context, ongoing, 0, warmup.Length, warmup);
        float[] reference = new float[128 + 4800 + 32];
        next.Render(context, next.CreateState(), 0, reference.Length, reference);
        previous.TryPrepareRunningState(next, ongoing).Should().BeTrue();
        previous.CommitPreparedRunningState(next, ongoing);
        float[] actual = new float[4800 + 32];
        next.Render(context, ongoing, 128, actual.Length, actual);

        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp(i / 4800.0, 0.0, 1.0);
            double w = t * t * (3 - 2 * t);
            double expected = warmup[^1] * (1 - w)
                + reference[128 + i] * w;
            actual[i].Should().BeApproximately((float)expected, 3e-6f,
                "the generic single-Oscillator fade must share the"
                + " same 100 ms sample-rate-scaled smoothstep clock");
        }
    }

    [Test]
    public void MultiDistributionEditStillRampsAfterTheLegacy64Frames()
    {
        FmSynthSound before = Multi(20);
        FmSynthSound after = Multi(500);
        RenderContext context = Mono(48000);
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);

        float[] actual = new float[4800 + 32];
        float[] old = new float[128 + 4800 + 32];
        float[] fresh = new float[128 + 4800 + 32];
        before.Render(context, before.CreateState(), 0, old.Length, old);
        after.Render(context, after.CreateState(), 0, fresh.Length, fresh);
        after.Render(context, held, 128, actual.Length, actual);

        // A substantial detune change must remain in flight beyond the
        // former 64-sample limit, yet end at the exact authored layout.
        double earlyDistance = 0.0;
        double oldDistance = 0.0;
        for (int i = 64; i < 192; i++)
        {
            earlyDistance += Math.Abs(actual[i] - fresh[128 + i]);
            oldDistance += Math.Abs(actual[i] - old[128 + i]);
        }
        earlyDistance.Should().BeGreaterThan(oldDistance,
            "at 1–4 ms a 100 ms detune glide must still be closer to"
            + " the original waveform than the fully retuned waveform");
        for (int i = 4800; i < actual.Length; i++)
            actual[i].Should().BeApproximately(fresh[128 + i], 4e-5f,
                "the new Multi layout must eventually become exact");
    }

    private static FmSynthSound Oscillator(double hz,
        FmOscillatorWaveform waveform = FmOscillatorWaveform.Sine) =>
        new(new FmSynthGraph([
            new FmOscillatorNode(1, waveform, hz),
        ], 1), new None(), enableLiveMultiExpansion: true);

    private static FmSynthSound Multi(double detune) =>
        new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 390),
            new FmMultiNode(2, 1, voiceCount: 3, detune: detune,
                randomizePhase: false, gainCompensation: true),
        ], 2), new None(), enableLiveMultiExpansion: true);

    private static RenderContext Mono(int sampleRate) =>
        new(new RenderConfiguration(sampleRate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private sealed class None : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
