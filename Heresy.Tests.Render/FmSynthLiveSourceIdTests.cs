using System;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Filters;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthLiveSourceIdTests
{
    private static readonly ObjectId FirstId = (ObjectId)9911U;
    private static readonly ObjectId AliasId = (ObjectId)9912U;

    [Test]
    public void SameBoundSampleUnderDifferentSourceIdKeepsContinuousHeldPlayhead()
    {
        float[] samples = Enumerable.Range(0, 4800).Select(i =>
            (float)(0.2 * Math.Sin(i * 0.078) + 0.1 * Math.Cos(i * 0.041)))
            .ToArray();
        SampleSound sample = Sample(FirstId, samples);
        SourceResolver resolver = new(FirstId, sample, AliasId, sample);
        FmSynthSound original = Sound(FirstId, resolver);
        FmSynthSound revised = Sound(AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState continuousReference = original.CreateState();

        float[] first = new float[257];
        original.Render(context, held, 0, first.Length, first);
        original.Render(context, continuousReference, 0, first.Length,
            new float[first.Length]);

        // The changed SourceId points to the very same resolved source.
        // Recreating the Sample invocation would throw away the already
        // integrated fractional playhead for no semantic reason.
        original.CanAdoptRunningState(revised, held).Should().BeTrue();
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] actual = new float[407], expected = new float[407];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 257, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        original.Render(context, continuousReference, 257, expected.Length,
            expected);
        actual.Should().Equal(expected,
            "a SourceId alias must not rewind or replace the original Sample history");

        // A second live selection back to the first ID is also an alias,
        // not a reason to abandon this held generation.
        FmSynthSound returned = Sound(FirstId, resolver);
        revised.TryPrepareRunningState(returned, held).Should().BeTrue();
        revised.CommitPreparedRunningState(returned, held);
        float[] later = new float[100], laterReference = new float[100];
        returned.Render(context, held, 664, later.Length, later);
        original.Render(context, continuousReference, 664,
            laterReference.Length, laterReference);
        later.Should().Equal(laterReference);
    }

    [Test]
    public void DifferentSampleSourceIdRequiresWorkerAndCrossfadesFreshOnset()
    {
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound differentSample = Sample(AliasId,
            Enumerable.Repeat(0.75f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId,
            differentSample);
        FmSynthSound original = Sound(FirstId, resolver);
        FmSynthSound revised = Sound(AliasId, resolver);
        SoundState held = original.CreateState();
        RenderContext context = Mono();
        original.Render(context, held, 0, 128, new float[128]);

        original.CanAdoptRunningState(revised, held).Should().BeFalse(
            "a new Sample cannot inherit the old Sample state or be constructed in PCM");
        original.TryPrepareRunningState(revised, held).Should().BeFalse();
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue(
            "the existing bounded worker can build the replacement Source invocation");
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);

        float[] output = new float[4801];
        long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - beforeAllocation).Should().Be(0);
        output[0].Should().BeApproximately(0.25f, 1e-6f,
            "the first replaced frame is the outgoing Sample, without a click");
        output[2400].Should().BeApproximately(0.5f, 1e-5f,
            "the replacement fades on the shared 100 ms smoothstep clock");
        output[4800].Should().BeApproximately(0.75f, 1e-5f,
            "after the fade the new Sample owns the entire output");
        // A live pitch curve cannot project a Sample EOF before reaching it.
        // Render through the actual end, then assert the new local clock.
        float[] throughEof = new float[11201];
        long eofAllocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 4929, throughEof.Length, throughEof);
        (GC.GetAllocatedBytesForCurrentThread() - eofAllocation).Should().Be(0);
        throughEof[11198].Should().BeApproximately(0.75f, 1e-6f);
        throughEof[11199].Should().Be(0.0f);
        revised.GetEndFrameExclusive(context, held).Should().Be(16128,
            "observed new Sample EOF belongs to its fresh onset at frame 128");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void ShortReplacementSampleEofCannotEraseAudibleOutgoingFade()
    {
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound shortSample = Sample(AliasId,
            Enumerable.Repeat(0.75f, 100).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId,
            shortSample);
        FmSynthSound original = Sound(FirstId, resolver);
        FmSynthSound revised = Sound(AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        PrepareReplacement(original, revised, held);
        float[] result = new float[4802];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, 200, result.AsSpan(0, 200));
        // Once the new 100-frame Sample's EOF was actually observed, its
        // silence must not remove the old Sample before the full fade.
        revised.GetEndFrameExclusive(context, held).Should().Be(4928,
            "outgoing Sample must carry the fade after new Sample EOF");
        revised.Render(context, held, 328, result.Length - 200,
            result.AsSpan(200));
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        result[0].Should().BeApproximately(0.25f, 1e-6f);
        result[101].Should().BeGreaterThan(0.24f,
            "old Sample must remain audible after the replacement reaches EOF");
        result[2400].Should().BeApproximately(0.125f, 1e-5f);
        result[4800].Should().Be(0.0f);
        revised.GetEndFrameExclusive(context, held).Should().Be(228,
            "once the fade ends the outgoing Sample no longer extends the new EOF");
    }

    [Test]
    public void SourceReplacementNoteOffCutsBothSampleStatesAtOriginalBoundary()
    {
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound differentSample = Sample(AliasId,
            Enumerable.Repeat(0.75f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId,
            differentSample);
        FmSynthSound original = Sound(FirstId, resolver);
        FmSynthSound revised = Sound(AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        PrepareReplacement(original, revised, held);
        held.NoteOffTime = TimeSpan.FromSeconds(200.0 / 48000);

        float[] output = new float[160];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        output[71].Should().BeGreaterThan(0.2f);
        output[72].Should().Be(0.0f,
            "the existing definite Sample Note Off is exclusive at frame 200");
        output.Skip(72).Should().OnlyContain(x => x == 0f);
        revised.GetEndFrameExclusive(context, held).Should().Be(200);
    }

    private static void PrepareReplacement(
        FmSynthSound original, FmSynthSound revised, SoundState held)
    {
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        worker.Fault.Should().BeNull();
    }

    private static SampleSound Sample(ObjectId id, float[] data)
        => new(new SampleDefinition(id, "SourceId migration sample",
            new ExternalAssetReference("source-id-test.wav")),
            new MemorySampleData(48000, 1, data));

    private static FmSynthSound Sound(ObjectId source, SourceResolver resolver)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, source, FmMultiNode.NeutralFrequencyHz),
        ], 1), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private static RenderContext Mono() => new(new RenderConfiguration(48000, [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class SourceResolver(ObjectId firstId, SampleSound first,
        ObjectId otherId, SampleSound other)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = id == firstId ? first : id == otherId ? other : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
