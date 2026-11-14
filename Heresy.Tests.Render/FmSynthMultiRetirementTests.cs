using System;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.Samples;
using Heresy.Render.Samples;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthMultiRetirementTests
{
    // This tests actual child-render progress, not merely a gain of zero:
    // the latter regressed when an inaudible 17-voice Multi continued
    // consuming all 17 child renderers after it was shrunk to three.
    [Test]
    public void ThreeToSeventeenToThreeStopsAdvancingFourteenRetiredChildren()
    {
        FmSynthSound Sound(int count) => new(Graph(count),
            new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        FmSynthSound three = Sound(3);
        SoundState held = three.CreateState();
        three.Render(context, held, 0, 80, new float[80]);

        FmSynthSound seventeen = Sound(17);
        PrepareExpansion(three, seventeen, held);
        three.TryPrepareRunningState(seventeen, held).Should().BeTrue();
        three.CommitPreparedRunningState(seventeen, held);
        seventeen.Render(context, held, 80, 4801, new float[4801]);

        FmSynthSound reduced = Sound(3);
        seventeen.TryPrepareRunningState(reduced, held).Should().BeTrue();
        seventeen.CommitPreparedRunningState(reduced, held);
        reduced.Render(context, held, 4881, 4801, new float[4801]);
        long[] atFadeEnd = ChildNextFrames(held);
        atFadeEnd.Should().HaveCount(17);
        atFadeEnd.Skip(3).Should().OnlyContain(x => x > 0,
            "all 17 children were genuinely rendered before retirement");

        float[] continuation = new float[1000];
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        reduced.Render(context, held, 9682, continuation.Length, continuation);
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long[] after = ChildNextFrames(held);
        after.Take(3).Zip(atFadeEnd.Take(3))
            .Should().OnlyContain(x => x.First > x.Second);
        after.Skip(3).Should().Equal(atFadeEnd.Skip(3),
            "zero-gain retired children must not continue expensive DSP");
        // The retirement path itself must not allocate in the PCM owner.
        // Allocation due to test-created output arrays is excluded above.
        (afterAlloc - beforeAlloc).Should().Be(0);
    }

    [Test]
    public void FullyRetiredConstituentsRequireWorkerPreparedFreshRevival()
    {
        FmSynthSound Sound(int count) => new(Graph(count),
            new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        FmSynthSound two = Sound(2);
        SoundState held = two.CreateState();
        two.Render(context, held, 0, 80, new float[80]);

        FmSynthSound four = Sound(4);
        PrepareExpansion(two, four, held);
        two.TryPrepareRunningState(four, held).Should().BeTrue();
        two.CommitPreparedRunningState(four, held);
        four.Render(context, held, 80, 4801, new float[4801]);

        FmSynthSound reduced = Sound(2);
        four.TryPrepareRunningState(reduced, held).Should().BeTrue();
        four.CommitPreparedRunningState(reduced, held);
        reduced.Render(context, held, 4881, 4801, new float[4801]);
        long[] retired = ChildNextFrames(held);
        reduced.CanAdoptRunningState(four, held).Should().BeFalse(
            "a stale halted state must never replay thousands of frames in PCM");
        reduced.RequestMultiExpansionPreparation(four, held).Should().BeTrue();

        using FirPreparationScheduler worker = new();
        reduced.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        reduced.AdoptPreparedFirControls(held);
        reduced.TryPrepareRunningState(four, held).Should().BeTrue();
        reduced.CommitPreparedRunningState(four, held);
        float[] revived = new float[4801];
        four.Render(context, held, 9682, revived.Length, revived);
        revived[0].Should().BeApproximately(0.5f, 1e-5f);
        revived[^1].Should().BeApproximately(1.0f, 1e-5f);
        ChildNextFrames(held).Skip(2).Should().OnlyContain(
            x => x == 4801,
            "revived states start a fresh, local lifetime, not a stale catch-up");
    }


    [Test]
    public void NestedMultiRevivalAfterCompleteRetirementPreservesParentVoices()
    {
        FmSynthSound Sound(int innerCount) => new(new FmSynthGraph([
            new FmConstantNode(1, 0.1),
            new FmMultiNode(2, 1, voiceCount: innerCount, detune: 0,
                randomizePhase: false, gainCompensation: false),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        FmSynthSound original = Sound(3);
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 80, new float[80]);

        FmSynthSound reduced = Sound(1);
        original.TryPrepareRunningState(reduced, held).Should().BeTrue();
        original.CommitPreparedRunningState(reduced, held);
        reduced.Render(context, held, 80, 4801, new float[4801]);
        float[] settled = new float[32];
        reduced.Render(context, held, 4881, settled.Length, settled);
        settled.Should().OnlyContain(x => Math.Abs(x - 0.2f) < 2e-6f,
            "two surviving outer constituents each retain one inner child");

        FmSynthSound revived = Sound(3);
        reduced.CanAdoptRunningState(revived, held).Should().BeFalse(
            "two independent inner Multis need detached reactivation states");
        reduced.RequestMultiExpansionPreparation(revived, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        reduced.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        reduced.AdoptPreparedFirControls(held);
        reduced.TryPrepareRunningState(revived, held).Should().BeTrue();
        reduced.CommitPreparedRunningState(revived, held);

        float[] comeback = new float[4801];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revived.Render(context, held, 4913, comeback.Length, comeback);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        comeback[0].Should().BeApproximately(0.2f, 2e-6f,
            "worker publication must not snap or restart the parent");
        comeback[^1].Should().BeApproximately(0.6f, 2e-6f,
            "all four reactivated inner children fade into the held note");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void SampleAndOnePoleMultiReviveFromFreshStateWithoutReplayingRetiredHistory()
    {
        ObjectId id = (ObjectId)9843U;
        float[] pcm = Enumerable.Range(0, 30000)
            .Select(i => (float)(0.3 + 0.2 * Math.Sin(i * 0.013))).ToArray();
        SampleSound sample = new(new SampleDefinition(id, "Retirement source",
            new ExternalAssetReference("retirement-source.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(id, sample);
        FilterDefinition definition = new((ObjectId)9844U, "IIR", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(520))])]);
        FmSynthSound Sound(int count) => new(new FmSynthGraph([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, new FilterInstance(definition),
                tailSeconds: 0.003),
            new FmMultiNode(3, 2, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 3), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

        RenderContext context = Mono();
        FmSynthSound two = Sound(2);
        SoundState held = two.CreateState();
        SoundState reference = two.CreateState();
        two.Render(context, held, 0, 80, new float[80]);
        FmSynthSound four = Sound(4);
        PrepareExpansion(two, four, held);
        two.TryPrepareRunningState(four, held).Should().BeTrue();
        two.CommitPreparedRunningState(four, held);
        four.Render(context, held, 80, 4801, new float[4801]);

        FmSynthSound reduced = Sound(2);
        four.TryPrepareRunningState(reduced, held).Should().BeTrue();
        four.CommitPreparedRunningState(reduced, held);
        reduced.Render(context, held, 4881, 4801, new float[4801]);
        long[] faded = ChildNextFrames(held);
        reduced.Render(context, held, 9682, 1000, new float[1000]);
        ChildNextFrames(held).Skip(2).Should().Equal(faded.Skip(2),
            "silent Sample readheads and IIR histories must stop advancing");

        FmSynthSound revived = Sound(4);
        reduced.CanAdoptRunningState(revived, held).Should().BeFalse();
        PrepareExpansion(reduced, revived, held);
        reduced.TryPrepareRunningState(revived, held).Should().BeTrue();
        reduced.CommitPreparedRunningState(revived, held);
        float[] expected = new float[15483];
        two.Render(context, reference, 0, expected.Length, expected);
        float[] actual = new float[4801];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revived.Render(context, held, 10682, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "reviving a filtered Sample cannot build or replay DSP in PCM");
        actual[0].Should().BeApproximately(expected[10682], 2e-6f,
            "both continuing Sample playheads and IIR states must stay exact");
        actual.Should().OnlyContain(float.IsFinite);
        Math.Abs(actual[^1] - expected[^1]).Should().BeGreaterThan(0.03f,
            "fresh reactivated Sample/Filter constituents become audible");
        ChildNextFrames(held).Skip(2).Should().OnlyContain(x => x == 4801,
            "newly reactivated Samples start at a new local onset");
    }

    private static void PrepareExpansion(
        FmSynthSound before, FmSynthSound after, SoundState held)
    {
        before.RequestMultiExpansionPreparation(after, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        before.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)).Should().BeTrue();
        before.AdoptPreparedFirControls(held);
    }

    private static long[] ChildNextFrames(SoundState state)
    {
        var multis = (Array)state.GetType().GetProperty("Multis")!
            .GetValue(state)!;
        var voices = multis.Cast<Array>().Single(x => x.Length > 0);
        return voices.Cast<object>().Select(voice =>
        {
            var invocation = (SoundInvocation)voice.GetType()
                .GetProperty("Invocation")!.GetValue(voice)!;
            return (long)invocation.State.GetType()
                .GetProperty("NextFrame")!.GetValue(invocation.State)!;
        }).ToArray();
    }

    private static FmSynthGraph Graph(int count) => new([
        new FmConstantNode(1, 0.25),
        new FmMultiNode(2, 1, voiceCount: count, detune: 0,
            randomizePhase: false, gainCompensation: false),
    ], 2);

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class SourceResolver(ObjectId id, SampleSound sample)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, bool mixdown, out ISound? sound)
        {
            sound = requested == id ? sample : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
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
