using System;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthLiveSampleOutputTests
{
    private static readonly ObjectId SampleId = (ObjectId)9250U;

    [TestCase(FmOperatorKind.Multiply, 0.90, 0.15)]
    [TestCase(FmOperatorKind.Add, 0.30, -0.20)]
    public void DirectSampleOutputScalarEditFadesWithoutRewindingTheSample(
        FmOperatorKind operation, double originalValue, double newValue)
    {
        SampleSound sample = Sample(600);
        FmSynthSound original = Sound(sample, originalValue, operation);
        FmSynthSound edited = Sound(sample, newValue, operation);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], actual = new float[192],
            reference = new float[320];
        original.Render(context, held, 0, 128, before);
        edited.Render(context, edited.CreateState(), 0, 320, reference);

        original.TryPrepareRunningState(edited, held).Should().BeTrue();
        original.CommitPreparedRunningState(edited, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        edited.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "editing an already-bound Sample's parent gain may not allocate in PCM");
        actual[0].Should().Be(before[^1]);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double weight = t * t * (3.0 - 2.0 * t);
            actual[i].Should().BeApproximately(
                (float)(before[^1] * (1.0 - weight)
                    + reference[128 + i] * weight), 3e-6f,
                "the original streaming Sample playhead must advance throughout"
                + " the 64-frame output transition, not restart at frame zero");
        }
    }

    [Test]
    public void SampleOutputOperatorEditFadesWhileKeepingItsNaturalEof()
    {
        SampleSound sample = Sample(400);
        FmSynthSound original = Sound(sample, 0.2, FmOperatorKind.Multiply);
        FmSynthSound revised = Sound(sample, 0.2, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], after = new float[320],
            reference = new float[448];
        original.Render(context, held, 0, before.Length, before);
        revised.Render(context, revised.CreateState(), 0, reference.Length, reference);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, after.Length, after);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        for (int i = 0; i < 64; i++)
        {
            double t = i / 64.0;
            double w = t * t * (3.0 - 2.0 * t);
            after[i].Should().BeApproximately(
                (float)(before[^1] * (1.0 - w)
                    + reference[128 + i] * w), 3e-6f);
        }
        for (int i = 64; i < after.Length; i++)
            after[i].Should().BeApproximately(reference[128 + i], 3e-6f,
                "the new Operator must follow the old Sample's forward playhead");

        // This test deliberately uses Add with a nonzero Constant. After
        // Sample EOF, the *graph* still has a live Constant audio branch;
        // compare to the revised graph rather than assuming a zero tail.
        revised.GetEndFrameExclusive(context, held).Should().Be(
            revised.GetEndFrameExclusive(context, revised.CreateState()),
            "the scalar edit must not invent an earlier Sample retirement");
    }

    [Test]
    public void SampleOutputGainEditPreservesNaturalEofAndDoesNotKeepPCMAlive()
    {
        SampleSound sample = Sample(400);
        FmSynthSound original = Sound(sample, 0.9);
        FmSynthSound revised = Sound(sample, 0.2);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] after = new float[320];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, after.Length, after);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        after[150].Should().NotBe(0.0f,
            "the running Sample continues playing after the output gain edit");
        for (int i = 272; i < after.Length; i++)
            after[i].Should().Be(0.0f,
                "the Sample must end at its unchanged 400-frame EOF");
        revised.GetEndFrameExclusive(context, held).Should().Be(400);
    }

    [Test]
    public void SampleOutputSecondGainEditStartsAtPreviouslyBlendedOutput()
    {
        SampleSound sample = Sample(600);
        FmSynthSound first = Sound(sample, 0.9, FmOperatorKind.Multiply);
        FmSynthSound second = Sound(sample, 0.25, FmOperatorKind.Multiply);
        FmSynthSound third = Sound(sample, 0.5, FmOperatorKind.Multiply);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        first.Render(context, held, 0, 128, new float[128]);
        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] partial = new float[16];
        second.Render(context, held, 128, partial.Length, partial);
        second.TryPrepareRunningState(third, held).Should().BeTrue();
        second.CommitPreparedRunningState(third, held);
        float[] reference = new float[240], output = new float[96];
        third.Render(context, third.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        third.Render(context, held, 144, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        output[0].Should().Be(partial[^1]);
        for (int i = 0; i < output.Length; i++)
        {
            double t = Math.Min(1.0, i / 64.0);
            double w = t * t * (3.0 - 2.0 * t);
            output[i].Should().BeApproximately(
                (float)(partial[^1] * (1 - w) + reference[144 + i] * w),
                3e-6f);
        }
    }

    [Test]
    public void NonAuditionSampleGainEditKeepsOriginalImmediateSemantics()
    {
        SampleSound sample = Sample(500);
        FmSynthSound original = Sound(sample, 0.9, audition: false);
        FmSynthSound next = Sound(sample, 0.2, audition: false);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        float[] refPcm = new float[129];
        next.Render(context, next.CreateState(), 0, refPcm.Length, refPcm);
        original.TryPrepareRunningState(next, held).Should().BeTrue();
        original.CommitPreparedRunningState(next, held);
        float[] actual = new float[1];
        next.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(refPcm[128], 3e-6f);
    }

    private static FmSynthSound Sound(SampleSound sample, double value,
        FmOperatorKind operation = FmOperatorKind.Multiply, bool audition = true)
    {
        SampleResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmConstantNode(2, value),
            new FmOperatorNode(3, operation, [1, 2]),
        ], 3), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static SampleSound Sample(int frames)
    {
        float[] pcm = Enumerable.Range(0, frames)
            .Select(i => (float)(0.3 + 0.14 * Math.Sin(i * 0.097)
                + 0.08 * Math.Cos(i * 0.037))).ToArray();
        return new(new SampleDefinition(SampleId, "Slice 53 source PCM",
            new ExternalAssetReference("slice53-source.wav")),
            new MemorySampleData(48000, 1, pcm));
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
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
