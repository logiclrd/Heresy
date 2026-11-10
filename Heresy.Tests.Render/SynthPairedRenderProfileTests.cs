using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

/// <summary>
/// Diagnostic, within-process A/B rendering profiles. Both arms run in the
/// same testhost, alternate first/second order and render independent but
/// identical voice timelines. Numerical timing is never a CI invariant.
///
/// The generic reference has an additional identity Multiply node: it is
/// PCM-equivalent, but NOT the same amount of DSP work. Its timing gives
/// a useful graph-complexity comparison, not an isolated estimate of the
/// direct-oscillator fast path's speedup.
/// </summary>
[TestFixture]
public sealed class SynthPairedRenderProfileTests
{
    private const int SampleRate = 48000;
    private const int Notes = 4;
    private const int MultiConstituents = 64;
    private const int BlockFrames = 256;
    private const int WarmupBlocks = 3;
    private const int DefaultPairedRounds = 7;
    private const int MaxPairedRounds = 512;
    // Long isolated recordings gather samples from a single JIT code path.
    // Do not extend CI's paired PCM test's duration or allocation footprint.
    private const int MaxIsolatedRounds = 65536;

    [Test]
    public void AlternatePairedSynthGraphShapesAndCheckEveryPcmBlock()
    {
        // A deliberately separate, opt-in CPU sampling mode exercises
        // exactly one renderer, so a perf recording cannot mix direct
        // and identity-Operator samples. Ordinary CI stays paired and
        // retains its byte-for-byte PCM assertions.
        string? focused = Environment.GetEnvironmentVariable(
            "HERESY_SYNTH_PROFILE_CASE");
        if (!string.IsNullOrWhiteSpace(focused))
        {
            ProfileSingleGraphShape(focused.Trim());
            return;
        }

        // The same-vs-same control measures order/scheduling noise. The
        // other pairs differ only by an inaudible identity Multiply node
        // after the oscillator; the actual waveform/phase math is shared.
        Compare("sine control", FmOscillatorWaveform.Sine,
            genericLeft: false, genericRight: false);
        Compare("sine identity operator", FmOscillatorWaveform.Sine,
            genericLeft: false, genericRight: true);
        Compare("square identity operator", FmOscillatorWaveform.Square,
            genericLeft: false, genericRight: true);
    }

    private static void ProfileSingleGraphShape(string name)
    {
        // Only the selected arm is ever created or rendered in this
        // invocation. Use one process per graph shape when sampling CPU:
        // otherwise JIT code and all three ordinary comparison pairs
        // would contribute to the same perf report.
        (FmOscillatorWaveform waveform, bool generic) = name switch
        {
            "sine-direct" => (FmOscillatorWaveform.Sine, false),
            "sine-generic" => (FmOscillatorWaveform.Sine, true),
            "square-direct" => (FmOscillatorWaveform.Square, false),
            "square-generic" => (FmOscillatorWaveform.Square, true),
            _ => throw new ArgumentException(
                "HERESY_SYNTH_PROFILE_CASE must be one of: " +
                "sine-direct, sine-generic, square-direct, square-generic."),
        };

        FmSynthSound sound = MakeSound(waveform, generic);
        SoundState[] states = CreateVoices(sound);
        RenderContext context = new(new RenderConfiguration(SampleRate,
        [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        float[] warmup = new float[WarmupBlocks * BlockFrames];
        RenderVoices(sound, states, context, 0, warmup.Length, warmup);

        int rounds = ProfileRounds(isolated: true);
        float[] pcm = new float[BlockFrames];
        double probe = 0.0;
        long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        long before = Stopwatch.GetTimestamp();
        for (int round = 0; round < rounds; round++)
        {
            Array.Clear(pcm);
            RenderVoices(sound, states, context,
                (WarmupBlocks + round) * BlockFrames, BlockFrames, pcm);
            // Keep the output observable without adding PCM assertions to
            // the hot loop or introducing any extra voices/graph paths.
            probe += pcm[round % BlockFrames];
        }
        long elapsedTicks = Stopwatch.GetTimestamp() - before;
        long allocation = GC.GetAllocatedBytesForCurrentThread()
            - beforeAllocation;
        Assert.That(double.IsFinite(probe), Is.True);
        foreach (float sample in pcm)
            Assert.That(float.IsFinite(sample), Is.True);

        double microseconds = (double)elapsedTicks * 1_000_000
            / Stopwatch.Frequency;
        TestContext.Progress.WriteLine(
            $"Isolated Synth renderer: {name}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}, " +
            $"OS={RuntimeInformation.OSDescription}, " +
            $"logical CPUs={Environment.ProcessorCount}, " +
            $"notes={Notes}, Multi children per note={MultiConstituents}, " +
            $"sample rate={SampleRate}, block frames={BlockFrames}, " +
            $"rounds={rounds}, us/output frame={microseconds / (rounds * BlockFrames):F3}, " +
            $"render-thread allocated bytes={allocation}, " +
            $"PCM probe={probe:R}");
    }

    private static void Compare(string label, FmOscillatorWaveform waveform,
        bool genericLeft, bool genericRight)
    {
        FmSynthSound a = MakeSound(waveform, genericLeft);
        FmSynthSound b = MakeSound(waveform, genericRight);
        SoundState[] statesA = CreateVoices(a);
        SoundState[] statesB = CreateVoices(b);
        RenderContext context = new(new RenderConfiguration(SampleRate,
        [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        float[] pcmA = new float[BlockFrames];
        float[] pcmB = new float[BlockFrames];
        // Warm up graph dispatch, JIT and invocation-owned histories outside
        // the timed window. The two arms start every measured round at the
        // exact same absolute frame, with independent oscillator phases.
        float[] warmup = new float[WarmupBlocks * BlockFrames];
        RenderVoices(a, statesA, context, 0, warmup.Length, warmup);
        Array.Clear(warmup);
        RenderVoices(b, statesB, context, 0, warmup.Length, warmup);

        int rounds = ProfileRounds();
        double[] microsA = new double[rounds];
        double[] microsB = new double[rounds];
        long allocatedA = 0, allocatedB = 0;
        for (int round = 0; round < rounds; round++)
        {
            int start = (WarmupBlocks + round) * BlockFrames;
            Array.Clear(pcmA);
            Array.Clear(pcmB);
            if (round % 2 == 0)
            {
                Measure(a, statesA, context, start, pcmA,
                    out microsA[round], out long allocA);
                allocatedA += allocA;
                Measure(b, statesB, context, start, pcmB,
                    out microsB[round], out long allocB);
                allocatedB += allocB;
            }
            else
            {
                Measure(b, statesB, context, start, pcmB,
                    out microsB[round], out long allocB);
                allocatedB += allocB;
                Measure(a, statesA, context, start, pcmA,
                    out microsA[round], out long allocA);
                allocatedA += allocA;
            }
            Assert.That(pcmB, Is.EqualTo(pcmA),
                $"paired {label}, round {round}: the reference's identity " +
                "Operator must not change any rendered PCM or voice timeline");
            foreach (float value in pcmA)
                Assert.That(float.IsFinite(value), Is.True);
        }

        double[] ratios = Enumerable.Range(0, rounds)
            .Select(i => microsB[i] / Math.Max(microsA[i], 1e-9))
            .ToArray();
        TestContext.Progress.WriteLine(
            $"Paired Synth renderer: {label}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}, " +
            $"OS={RuntimeInformation.OSDescription}, " +
            $"logical CPUs={Environment.ProcessorCount}, " +
            $"notes={Notes}, Multi children per note={MultiConstituents}, " +
            $"sample rate={SampleRate}, block frames={BlockFrames}, " +
            $"rounds={rounds}, A median us/frame={Median(microsA) / BlockFrames:F3}, " +
            $"B median us/frame={Median(microsB) / BlockFrames:F3}, " +
            $"paired B/A median={Median(ratios):F3}, " +
            $"paired B/A range={ratios.Min():F3}..{ratios.Max():F3}, " +
            $"render-thread bytes A={allocatedA}, B={allocatedB}");
    }

    /// <summary>
    /// Long per-shape sampling may need thousands of rounds. Keep the
    /// exact-PCM paired comparison capped at 512 rounds, even when the
    /// opt-in isolated profiler accepts larger counts. Neither path
    /// turns host-specific timing into a correctness assertion.
    /// </summary>
    private static int ProfileRounds(bool isolated = false)
    {
        string? requested = Environment.GetEnvironmentVariable(
            "HERESY_SYNTH_PROFILE_ROUNDS");
        int maximum = isolated ? MaxIsolatedRounds : MaxPairedRounds;
        return int.TryParse(requested, out int rounds)
            ? Math.Clamp(rounds, DefaultPairedRounds, maximum)
            : DefaultPairedRounds;
    }

    private static void Measure(FmSynthSound sound, SoundState[] voices,
        RenderContext context, int startFrame, float[] samples,
        out double microseconds, out long allocatedBytes)
    {
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        long before = Stopwatch.GetTimestamp();
        RenderVoices(sound, voices, context, startFrame,
            samples.Length, samples);
        long ticks = Stopwatch.GetTimestamp() - before;
        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        microseconds = (double)ticks * 1_000_000 / Stopwatch.Frequency;
    }

    private static void RenderVoices(FmSynthSound sound, SoundState[] voices,
        RenderContext context, int startFrame, int frames, float[] destination)
    {
        // ISound.Render adds into its caller's mix buffer, as the playback
        // producer does. A fresh zeroed bus is supplied per measured round.
        foreach (SoundState voice in voices)
            sound.Render(context, voice, startFrame, frames, destination);
    }

    private static SoundState[] CreateVoices(FmSynthSound sound)
    {
        SoundState[] voices = new SoundState[Notes];
        for (int i = 0; i < voices.Length; i++)
        {
            voices[i] = sound.CreateState();
            voices[i].SetNoiseSeed((ulong)(i + 11));
        }
        return voices;
    }

    private static FmSynthSound MakeSound(
        FmOscillatorWaveform waveform, bool generic)
    {
        FmOscillatorNode oscillator = new(1, waveform, 330,
            minimum: -0.7, maximum: 0.7, curvature: 1.25);
        FmSynthGraph graph = generic
            ? new FmSynthGraph([
                oscillator,
                new FmConstantNode(3, 1.0),
                new FmOperatorNode(4, FmOperatorKind.Multiply, [1, 3]),
                new FmMultiNode(2, 4, voiceCount: MultiConstituents,
                    detune: 0, randomizePhase: false,
                    gainCompensation: false),
            ], 2)
            : new FmSynthGraph([
                oscillator,
                new FmMultiNode(2, 1, voiceCount: MultiConstituents,
                    detune: 0, randomizePhase: false,
                    gainCompensation: false),
            ], 2);
        return new FmSynthSound(graph, new EmptyEnvelopes());
    }

    private static double Median(double[] measurements)
    {
        double[] ordered = measurements.OrderBy(x => x).ToArray();
        return ordered[ordered.Length / 2];
    }

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
