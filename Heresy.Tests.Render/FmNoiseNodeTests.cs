using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmNoiseNodeTests
{
    private const int Rate = 48000;
    private static readonly RenderContext Mono = new(new RenderConfiguration(Rate,
        [new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));
    private static readonly RenderContext Stereo =
        new(RenderConfiguration.Stereo(Rate));

    [Test]
    public void NoiseIsNativeRateWhiteAndHasNeitherPitchNorFrequencyInputs()
    {
        FmNoiseNode node = new(12);
        Assert.That(node.InputNodeIds, Is.Empty);
        FmSynthSound sound = Noise(node);
        SoundState state = sound.CreateState();
        state.SetNoiseSeed(0x0123456789ABCDEFUL);
        const int count = 32768;
        float[] samples = new float[count];
        sound.Render(Mono, state, 0, count, samples);
        double mean = samples.Average(x => (double)x);
        double power = samples.Average(x => (double)x * x);
        double lagOne = 0;
        for (int i = 1; i < samples.Length; i++)
            lagOne += (double)samples[i] * samples[i - 1];
        lagOne /= samples.Length - 1;
        Assert.Multiple(() =>
        {
            Assert.That(mean, Is.InRange(-0.025, 0.025));
            Assert.That(power, Is.InRange(0.30, 0.37),
                "uniform native-rate noise has expected power 1/3");
            Assert.That(Math.Abs(lagOne), Is.LessThan(0.025));
            Assert.That(samples.Min(), Is.LessThan(-0.95f));
            Assert.That(samples.Max(), Is.GreaterThan(0.95f));
        });

        // Resolve selected broad FFT-frequency bands without a DSP dependency.
        // Independent equal-spectrum white noise has similar average bin power.
        double[] energies = new double[8];
        for (int block = 0; block < 32; block++)
        {
            const int length = 512;
            for (int band = 0; band < energies.Length; band++)
            {
                int k = (band + 1) * 27;
                double real = 0, imag = 0;
                for (int t = 0; t < length; t++)
                {
                    double angle = 2 * Math.PI * k * t / length;
                    float amplitude = samples[block * length + t];
                    real += amplitude * Math.Cos(angle);
                    imag += amplitude * Math.Sin(angle);
                }
                energies[band] += real * real + imag * imag;
            }
        }
        double minBand = energies.Min();
        double maxBand = energies.Max();
        Assert.That(minBand, Is.GreaterThan(0));
        Assert.That(maxBand / minBand, Is.LessThan(2.5),
            "all low-to-Nyquist bands must carry significant unshaped noise power");
    }

    [Test]
    public void ChunkingPitchChangesBackwardSeekAndDirectReplayAreExact()
    {
        FmSynthSound sound = Noise(new FmNoiseNode(2));
        SoundState entire = sound.CreateState();
        SoundState split = sound.CreateState();
        entire.SetNoiseSeed(321UL);
        split.SetNoiseSeed(321UL);
        entire.PitchMultiplier = 3;
        entire.PlaybackSpeedMultiplier = 0.25;
        float[] expected = new float[4096];
        float[] actual = new float[4096];
        sound.Render(Mono, entire, 0, expected.Length, expected);
        int[] chunks = [1, 37, 5, 128, 9, 43];
        for (int index = 0, part = 0; index < actual.Length; part++)
        {
            int length = Math.Min(chunks[part % chunks.Length],
                actual.Length - index);
            sound.Render(Mono, split, index, length,
                actual.AsSpan(index, length));
            index += length;
        }
        Assert.That(actual, Is.EqualTo(expected));
        // Render adds to its destination, so clear the replayed subrange.
        actual.AsSpan(17, 231).Clear();
        sound.Render(Mono, split, 17, 231, actual.AsSpan(17, 231));
        Assert.That(actual, Is.EqualTo(expected),
            "a rewind must regenerate frame-indexed noise, not repeat a shifted PRNG stream");
        SoundState oppositePitch = sound.CreateState();
        oppositePitch.SetNoiseSeed(321UL);
        oppositePitch.PitchMultiplier = 0.125;
        oppositePitch.PlaybackSpeedMultiplier = 11;
        float[] pitchIndependent = new float[4096];
        sound.Render(Mono, oppositePitch, 0, pitchIndependent.Length, pitchIndependent);
        Assert.That(pitchIndependent, Is.EqualTo(expected));
    }

    [Test]
    public void DistinctVoicesAndNodesDoNotShareNoiseSequences()
    {
        FmSynthSound one = Noise(new FmNoiseNode(1));
        FmSynthSound two = Noise(new FmNoiseNode(2));
        SoundState voiceA = one.CreateState();
        SoundState voiceB = one.CreateState();
        SoundState alternateNode = two.CreateState();
        voiceA.SetNoiseSeed(10);
        voiceB.SetNoiseSeed(11);
        alternateNode.SetNoiseSeed(10);
        float[] a = new float[256], b = new float[256], c = new float[256];
        one.Render(Mono, voiceA, 0, a.Length, a);
        one.Render(Mono, voiceB, 0, b.Length, b);
        two.Render(Mono, alternateNode, 0, c.Length, c);
        Assert.Multiple(() =>
        {
            Assert.That(a.SequenceEqual(b), Is.False);
            Assert.That(a.SequenceEqual(c), Is.False);
            Assert.That(a.Zip(b, (x, y) => (double)x * y).Average(),
                Is.InRange(-0.07, 0.07));
        });
    }

    [Test]
    public void StereoIsOrdinaryMonoSourceSpatializationAndNoteOffStopsNoise()
    {
        FmSynthSound sound = Noise(new FmNoiseNode(0));
        SoundState mono = sound.CreateState(), stereo = sound.CreateState();
        mono.SetNoiseSeed(49);
        stereo.SetNoiseSeed(49);
        float[] x = new float[128], xy = new float[256];
        sound.Render(Mono, mono, 0, 128, x);
        sound.Render(Stereo, stereo, 0, 128, xy);
        for (int frame = 0; frame < x.Length; frame++)
        {
            Assert.That(xy[frame * 2], Is.EqualTo(xy[frame * 2 + 1]));
            Assert.That(xy[frame * 2], Is.EqualTo(x[frame] / Math.Sqrt(2))
                .Within(1e-6));
        }
        SoundState released = sound.CreateState();
        released.SetNoiseSeed(49);
        released.NoteOffTime = TimeSpan.FromSeconds(50.0 / Rate);
        float[] tail = new float[80];
        sound.Render(Mono, released, 0, 80, tail);
        Assert.That(tail.Take(50), Is.EqualTo(x.Take(50)));
        Assert.That(tail.Skip(51), Is.All.Zero);
    }

    [Test]
    public void SteadyNativeNoiseRenderingHasNoPerFrameHeapHistory()
    {
        FmSynthSound sound = Noise(new FmNoiseNode(0));
        SoundState state = sound.CreateState();
        float[] buffer = new float[128];
        sound.Render(Mono, state, 0, buffer.Length, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 256; i++)
            sound.Render(Mono, state, (long)i * 128, 128, buffer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(4096),
            "noise never materializes an unbounded per-voice PCM/RNG history");
    }

    private static FmSynthSound Noise(FmNoiseNode node)
        => new(new FmSynthGraph([node], node.Id), new MissingEnvelopeResolver());

    private sealed class MissingEnvelopeResolver : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
