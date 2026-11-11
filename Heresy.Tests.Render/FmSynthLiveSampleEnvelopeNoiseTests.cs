using System;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
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
public sealed class FmSynthLiveSampleEnvelopeNoiseTests
{
    private static readonly ObjectId SampleId = (ObjectId)9251U;
    private static readonly ObjectId EnvelopeId = (ObjectId)9252U;

    [Test]
    public void AudibleEnvelopeEditAroundContinuingSampleFadesWithoutRestart()
    {
        SampleSound sample = Sample();
        FmSynthSound original = EnvelopeGain(sample, 0.9);
        FmSynthSound revised = EnvelopeGain(sample, 0.15);
        RenderContext context = Mono();
        SoundState live = original.CreateState();
        float[] previous = new float[128], actual = new float[160],
            reference = new float[288];
        original.Render(context, live, 0, previous.Length, previous);
        revised.Render(context, revised.CreateState(), 0,
            reference.Length, reference);

        original.TryPrepareRunningState(revised, live).Should().BeTrue();
        original.CommitPreparedRunningState(revised, live);
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, live, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocations).Should().Be(0L);

        AssertOutputFade(previous[^1], reference, actual, 128);
    }

    [Test]
    public void LiveEnvelopeUsedOnlyForSamplePitchDoesNotHoldTheOutput()
    {
        SampleSound sample = Sample();
        FmSynthSound before = PitchEnvelope(sample, 1.0);
        FmSynthSound after = PitchEnvelope(sample, 2.0);
        RenderContext context = Mono();
        SoundState live = before.CreateState();
        before.Render(context, live, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, live).Should().BeTrue();
        before.CommitPreparedRunningState(after, live);
        float[] actual = new float[3];
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, live, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocations).Should().Be(0L);

        // The original Sample playhead at frame 128 must survive the
        // curve edit; the new multiplier changes only *future* rate.
        // Do not use an audible 64-frame hold in this control-only case.
        SampleSound referenceSample = Sample();
        FmSynthSound reference = PitchEnvelope(referenceSample, 1.0);
        SoundState referenceState = reference.CreateState();
        float[] originalPcm = new float[131];
        reference.Render(context, referenceState, 0, originalPcm.Length, originalPcm);
        actual[0].Should().BeApproximately(originalPcm[128], 2e-6f);
        Math.Abs(actual[2] - originalPcm[130]).Should().BeGreaterThan(0.001f,
            "changing a Sample rate control must alter the forward"
            + " playhead rather than smearing the output with a fade");
    }

    [Test]
    public void AudibleConstantEditInSampleAndNoiseMixCrossfadesWholeOutput()
    {
        SampleSound sample = Sample();
        FmSynthSound original = SampleNoiseMix(sample, 0.9);
        FmSynthSound revised = SampleNoiseMix(sample, 0.1);
        RenderContext context = Mono();
        SoundState live = original.CreateState();
        SoundState fresh = revised.CreateState();
        live.SetNoiseSeed(53);
        fresh.SetNoiseSeed(53);
        float[] previous = new float[128], actual = new float[160],
            reference = new float[288];
        original.Render(context, live, 0, previous.Length, previous);
        revised.Render(context, fresh, 0, reference.Length, reference);
        original.TryPrepareRunningState(revised, live).Should().BeTrue();
        original.CommitPreparedRunningState(revised, live);
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, live, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocations).Should().Be(0L);
        AssertOutputFade(previous[^1], reference, actual, 128);
    }

    [Test]
    public void SampleAndNoiseMixOrdinaryPlaybackKeepsImmediateGain()
    {
        SampleSound sample = Sample();
        FmSynthSound original = SampleNoiseMix(sample, 0.9, audition: false);
        FmSynthSound revised = SampleNoiseMix(sample, 0.1, audition: false);
        RenderContext context = Mono();
        SoundState live = original.CreateState(), fresh = revised.CreateState();
        live.SetNoiseSeed(123);
        fresh.SetNoiseSeed(123);
        original.Render(context, live, 0, 128, new float[128]);
        float[] expected = new float[129], actual = new float[1];
        revised.Render(context, fresh, 0, expected.Length, expected);
        original.TryPrepareRunningState(revised, live).Should().BeTrue();
        original.CommitPreparedRunningState(revised, live);
        revised.Render(context, live, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[128], 2e-6f);
    }

    private static void AssertOutputFade(float previous, float[] reference,
        float[] actual, int start)
    {
        actual[0].Should().Be(previous);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3 - 2 * t);
            actual[i].Should().BeApproximately(
                (float)(previous * (1 - w)
                    + reference[start + i] * w), 3e-6f);
        }
    }

    private static FmSynthSound EnvelopeGain(SampleSound sample, double level)
    {
        SourceResolver resolver = new(sample, Curve(level));
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmEnvelopeNode(2, EnvelopeId),
            new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
        ], 3), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);
    }

    private static FmSynthSound PitchEnvelope(SampleSound sample, double level)
    {
        SourceResolver resolver = new(sample, Curve(level));
        return new(new FmSynthGraph([
            new FmEnvelopeNode(2, EnvelopeId),
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz,
                multiplierNodeId: 2),
        ], 1), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);
    }

    private static FmSynthSound SampleNoiseMix(SampleSound sample,
        double gain, bool audition = true)
    {
        SourceResolver resolver = new(sample, Curve(1));
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmConstantNode(2, gain),
            new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
            new FmNoiseNode(4),
            new FmOperatorNode(5, FmOperatorKind.Add, [3, 4]),
        ], 5), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static NodeEnvelopeCurve Curve(double level)
    {
        NodeEnvelopeDefinition d = new(EnvelopeId, "Shared");
        d.ReplaceCurve([
            new(TimeSpan.Zero, level),
            new(TimeSpan.FromMilliseconds(10), level),
            new(TimeSpan.FromMilliseconds(11), level),
            new(TimeSpan.FromMilliseconds(100), 0),
        ], 2, 2, EnvelopeSustainLoopMode.Forward);
        return new(d);
    }

    private static SampleSound Sample()
    {
        float[] pcm = Enumerable.Range(0, 1024)
            .Select(i => (float)(0.31 * Math.Sin(i * 0.097)
                + 0.12 * Math.Cos(i * 0.043))).ToArray();
        return new(new SampleDefinition(SampleId, "Noise context",
            new ExternalAssetReference("slice54.wav")),
            new MemorySampleData(48000, 1, pcm));
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class SourceResolver(SampleSound sample,
        NodeEnvelopeCurve envelope) : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId sourceId, bool mixdown,
            out ISound? sound)
        {
            sound = sourceId == SampleId ? sample : null;
            return sound is not null;
        }

        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = id == EnvelopeId ? envelope : null;
            return curve is not null;
        }
    }
}
