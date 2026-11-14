using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
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
    private static readonly ObjectId ThirdId = (ObjectId)9913U;
    private static readonly ObjectId FourthId = (ObjectId)9914U;

    [Test]
    public void SampleReplacementInsideMultiFadesEachVoiceBeforeContinuingParentFilter()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = OnePole();
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.30f, 16000).ToArray());
        SampleSound incomingSample = Sample(AliasId,
            Enumerable.Repeat(-0.10f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId, incomingSample);

        FmSynthSound Build(ObjectId sourceId, SourceResolver sounds)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, sourceId, FmMultiNode.NeutralFrequencyHz),
                new FmMultiNode(2, 1, voiceCount: 3, detune: 0,
                    randomizePhase: false, gainCompensation: true),
                new FmFilterNode(3, 2, filter, tailSeconds: 0.003),
            ], 3), sounds, soundResolver: sounds,
                enableLiveMultiExpansion: true);

        FmSynthSound before = Build(FirstId, resolver);
        FmSynthSound after = Build(AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, start, new float[start]);

        // Each Multi constituent owns its own SourceVoice and Sample readhead.
        // Preparation may complete at different times; no constituent may
        // partially adopt a SourceId change or restart the shared Filter.
        before.RequestMultiExpansionPreparation(after, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        before.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() =>
        {
            before.AdoptPreparedFirControls(held);
            return before.CanAdoptRunningState(after, held);
        }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        worker.Fault.Should().BeNull();

        // Three equal-frequency/phase constituents are gain-normalized to
        // one source. An independent reference bakes the expected 100 ms
        // Source-local fade and passes it through the SAME Multi + Filter.
        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = i < start ? 0.30f
                : (float)(0.30 * (1 - w) - 0.10 * w);
        }
        SampleSound referenceSample = Sample(FirstId, baked);
        SourceResolver referenceResolver = new(FirstId, referenceSample,
            AliasId, referenceSample);
        FmSynthSound reference = Build(FirstId, referenceResolver);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);

        float[] actual = new float[baked.Length - start];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 5e-5f,
                $"every child Source fade must enter one uninterrupted parent Filter at {start + i}");
    }

    [Test]
    public void ConnectedSourceRateModulationDoesNotBlockSampleReplacement()
    {
        const int start = 128, fadeFrames = 4800;
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound replacementSample = Sample(AliasId,
            Enumerable.Repeat(0.75f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId,
            replacementSample);

        FmSynthSound Build(ObjectId id) => new(new FmSynthGraph([
            new FmConstantNode(1, 1.25),
            new FmSoundSourceNode(2, id, FmMultiNode.NeutralFrequencyHz,
                multiplierNodeId: 1),
        ], 2), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

        FmSynthSound original = Build(FirstId), revised = Build(AliasId);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        // A wired but unchanged rate control cannot invalidate an
        // independently prepared, new Sample onset and source-level fade.
        float[] actual = new float[fadeFrames + 1];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        actual[0].Should().BeApproximately(0.25f, 1e-6f);
        actual[fadeFrames / 2].Should().BeApproximately(0.50f, 1e-5f);
        actual[fadeFrames].Should().BeApproximately(0.75f, 1e-5f);
    }

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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ChangedSampleAndFrequencyBasisCrossfadeAtIndependentReadheadsBeforeOnePole(
        bool newConstant, bool newExponential)
    {
        const int start = 128, fade = 4800;
        double neutral = FmMultiNode.NeutralFrequencyHz;
        float[] oldData = Enumerable.Range(0, 20000)
            .Select(i => (float)(0.08 + (i % 103) * 0.006))
            .ToArray();
        float[] newData = Enumerable.Range(0, 20000)
            .Select(i => (float)(0.12 + (i % 157) * 0.004))
            .ToArray();
        SampleSound oldSample = Sample(FirstId, oldData);
        SampleSound newSample = Sample(AliasId, newData);
        SourceResolver resolver = new(FirstId, oldSample, AliasId, newSample);
        FilterInstance filter = OnePole();

        FmSynthSound Build(ObjectId id, double hz, bool constant,
            bool exponential, SourceResolver sounds)
            => new(new FmSynthGraph([
                new FmConstantNode(0, 1.25),
                new FmSoundSourceNode(1, id, hz, multiplierNodeId: 0,
                    constant: constant, exponentialMultiplier: exponential),
                new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
            ], 2), sounds, soundResolver: sounds,
                enableLiveMultiExpansion: true);
        FmSynthSound Bare(ObjectId id, double hz, bool constant, bool exponential)
            => new(new FmSynthGraph([
                new FmConstantNode(0, 1.25),
                new FmSoundSourceNode(1, id, hz, multiplierNodeId: 0,
                    constant: constant, exponentialMultiplier: exponential),
            ], 1), resolver, soundResolver: resolver,
                enableLiveMultiExpansion: true);
        FmSynthSound original = Build(FirstId, neutral, false, false, resolver);
        FmSynthSound revised = Build(AliasId, neutral * 2,
            newConstant, newExponential, resolver);
        SoundState held = original.CreateState();
        RenderContext context = Mono();

        // Two independently rendered Source notes supply the oracle:
        // A continues at its ORIGINAL 1.25x linear modulation and
        // existing readhead. B starts at the revised 2x frequency basis
        // and authored Constant/exponential mode at the edit frame.
        // Neither side is reconstructed from the other's readhead.
        FmSynthSound bareOld = Bare(FirstId, neutral, false, false);
        FmSynthSound bareNew = Bare(AliasId, neutral * 2,
            newConstant, newExponential);
        float[] outgoing = new float[start + fade + 64];
        float[] incoming = new float[fade + 64];
        bareOld.Render(context, bareOld.CreateState(), 0,
            outgoing.Length, outgoing);
        bareNew.Render(context, bareNew.CreateState(), 0,
            incoming.Length, incoming);
        float[] blended = new float[outgoing.Length];
        for (int frame = 0; frame < blended.Length; frame++)
        {
            if (frame < start)
            {
                blended[frame] = outgoing[frame];
                continue;
            }
            int local = frame - start;
            double t = Math.Clamp((double)local / fade, 0, 1);
            double w = t * t * (3 - 2 * t);
            blended[frame] = (float)(outgoing[frame] * (1 - w)
                + incoming[local] * w);
        }
        SampleSound baked = Sample(FirstId, blended);
        SourceResolver oracleResolver = new(FirstId, baked, AliasId, baked);
        // The baked reference is already on the output-frame clock:
        // do NOT apply the original 1.25x modulation a second time.
        FmSynthSound oracle = FilteredSound(FirstId, oracleResolver, filter);
        float[] reference = new float[blended.Length];
        oracle.Render(context, oracle.CreateState(), 0,
            reference.Length, reference);

        original.Render(context, held, 0, start, new float[start]);
        original.CanPrepareMultiExpansion(revised, held).Should().BeTrue(
            "a frequency-basis edit belongs to the NEW Sample onset, not to the old A readhead");
        PrepareReplacement(original, revised, held);
        float[] actual = new float[fade + 64];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0L,
            "both independent Source playheads and the shared OnePole are PCM-owned");
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(reference[start + i], 3e-5f,
                $"A's original linear pitch, B's revised frequency/mode, and the continuous pole at frame {start + i}");
        actual[0].Should().BeApproximately(reference[start], 1e-6f);
        actual[fade / 2].Should().BeApproximately(reference[start + fade / 2], 3e-5f);
        actual[fade].Should().BeApproximately(reference[start + fade], 3e-5f);
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
        long firstRenderBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
        firstRenderBytes.Should().Be(0);
        // Once the new 100-frame Sample's EOF was actually observed, its
        // silence must not remove the old Sample before the full fade.
        revised.GetEndFrameExclusive(context, held).Should().Be(4928,
            "outgoing Sample must carry the fade after new Sample EOF");
        allocation = GC.GetAllocatedBytesForCurrentThread();
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

    [TestCase(false)]
    [TestCase(true)]
    public void NoteOffDuringSampleReplacementKeepsBothReleaseContributionsAndFilterTail(
        bool insideMulti)
    {
        const int edit = 128, noteOff = 1900, incomingLength = 2000;
        const int releaseFrames = 800;
        const int fade = 4800;
        ObjectId envelopeId = (ObjectId)9923U;
        NodeEnvelopeDefinition definition = new(envelopeId,
            "Sustained release through ongoing Sample replacement");
        definition.ReplaceCurve([
            new(TimeSpan.Zero, 1.0),
            new(TimeSpan.FromMilliseconds(1), 1.0),
            new(TimeSpan.FromMilliseconds(12), 0.0),
        ], 1, 1, EnvelopeSustainLoopMode.Forward);
        NodeEnvelopeCurve release = new(definition);
        IEnvelopeCurveResolver curves = new OneCurve(envelopeId, release);
        FilterInstance filter = OnePole();

        SampleSound outgoing = Sample(FirstId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound incoming = Sample(AliasId,
            Enumerable.Repeat(0.75f, incomingLength).ToArray());
        SourceResolver resolver = new(FirstId, outgoing, AliasId, incoming);

        FmSynthSound Build(ObjectId selected, SourceResolver samples)
        {
            // A sustain/release envelope belongs to the parent Synth,
            // and the Source swap occurs BEFORE this continuing OnePole.
            // The optional Multi has three independent equal-pitch
            // Source invocation states, not one shared Sample playhead.
            FmSynthGraph graph = insideMulti
                ? new([
                    new FmSoundSourceNode(1, selected,
                        FmMultiNode.NeutralFrequencyHz),
                    new FmMultiNode(2, 1, voiceCount: 3, detune: 0,
                        randomizePhase: false, gainCompensation: true),
                    new FmEnvelopeNode(3, envelopeId),
                    new FmOperatorNode(4, FmOperatorKind.Multiply, [2, 3]),
                    new FmFilterNode(5, 4, filter, tailSeconds: 0.003),
                ], 5)
                : new([
                    new FmSoundSourceNode(1, selected,
                        FmMultiNode.NeutralFrequencyHz),
                    new FmEnvelopeNode(3, envelopeId),
                    new FmOperatorNode(4, FmOperatorKind.Multiply, [1, 3]),
                    new FmFilterNode(5, 4, filter, tailSeconds: 0.003),
                ], 5);
            return new(graph, curves, soundResolver: samples,
                enableLiveMultiExpansion: true);
        }

        FmSynthSound original = Build(FirstId, resolver);
        FmSynthSound revised = Build(AliasId, resolver);

        // Independent reference bakes the TWO unsilenced Sample
        // contributions before the shared envelope and IIR. The
        // incoming Sample ends at global frame 2128, AFTER Note Off,
        // while the outgoing Sample continues through the release.
        float[] baked = new float[10000];
        for (int frame = 0; frame < baked.Length; frame++)
        {
            if (frame < edit)
            {
                baked[frame] = 0.25f;
                continue;
            }
            double t = Math.Clamp((double)(frame - edit) / fade, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[frame] = (float)(0.25 * (1 - w)
                + (frame - edit < incomingLength ? 0.75 * w : 0));
        }
        SampleSound referencePcm = Sample(FirstId, baked);
        SourceResolver referenceResolver = new(FirstId, referencePcm,
            AliasId, referencePcm);
        FmSynthSound oracle = Build(FirstId, referenceResolver);

        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState expectedState = oracle.CreateState();
        original.Render(context, held, 0, edit, new float[edit]);
        oracle.Render(context, expectedState, 0, edit, new float[edit]);
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        SpinWait.SpinUntil(() =>
        {
            original.AdoptPreparedFirControls(held);
            return original.CanAdoptRunningState(revised, held);
        }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        worker.Fault.Should().BeNull();

        int heldFrames = noteOff - edit;
        float[] beforeOff = new float[heldFrames];
        float[] expectedHeld = new float[heldFrames];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, edit, heldFrames, beforeOff);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0L);
        oracle.Render(context, expectedState, edit, heldFrames, expectedHeld);
        for (int i = 0; i < heldFrames; i++)
            beforeOff[i].Should().BeApproximately(expectedHeld[i], 5e-5f,
                $"both Source playheads and IIR history before Off at {edit + i}");

        TimeSpan offTime = TimeSpan.FromSeconds((double)noteOff / 48000);
        held.NoteOffTime = expectedState.NoteOffTime = offTime;
        float[] released = new float[releaseFrames];
        float[] expectedRelease = new float[releaseFrames];
        allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, noteOff, releaseFrames, released);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0L,
            "Note Off during a running source fade must not allocate PCM state");
        oracle.Render(context, expectedState, noteOff, releaseFrames,
            expectedRelease);
        for (int i = 0; i < releaseFrames; i++)
            released[i].Should().BeApproximately(expectedRelease[i], 5e-5f,
                $"incoming/outgoing release and parent Filter at {noteOff + i}");

        released[10].Should().BeGreaterThan(0.3f,
            "both Samples contribute just after Note Off");
        released[incomingLength + edit - noteOff + 40]
            .Should().BeGreaterThan(0.01f,
                "outgoing Sample still releases after incoming EOF");
        long expectedEnd = release.GetEndActiveFrameExclusiveAfterNoteOff(
            noteOff, 48000)!.Value + 144; // authored 3 ms Filter tail
        oracle.GetEndFrameExclusive(context, expectedState)
            .Should().Be(expectedEnd);
        revised.GetEndFrameExclusive(context, held).Should().Be(expectedEnd,
            "the outgoing fade cannot prolong release or remove the IIR tail");
        released.Skip((int)(expectedEnd - noteOff))
            .Should().OnlyContain(value => value == 0f,
                "both Source histories and the authored tail must retire");
        worker.Fault.Should().BeNull();
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


    [Test]
    public void AddMixerRetargetsOneSampleWithoutRewindingItsIndependentSibling()
    {
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.12f, 16000).ToArray());
        float[] independentPcm = Enumerable.Range(0, 18000)
            .Select(i => (float)(0.10 * Math.Sin(i * 0.053))).ToArray();
        SampleSound independent = Sample(AliasId, independentPcm);
        SampleSound replacement = Sample(ThirdId,
            Enumerable.Repeat(0.34f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, independent), (ThirdId, replacement));
        FmSynthSound original = MixSound(FirstId, AliasId, resolver);
        FmSynthSound revised = MixSound(ThirdId, AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState uninterrupted = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.Render(context, uninterrupted, 0, 128, new float[128]);

        original.CanAdoptRunningState(revised, held).Should().BeFalse(
            "no changed Sample may inherit the old Source invocation");
        PrepareReplacement(original, revised, held);
        float[] actual = new float[4801], baseline = new float[4801];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        original.Render(context, uninterrupted, 128, baseline.Length, baseline);
        for (int i = 0; i < actual.Length; i++)
        {
            double progress = i / 4800.0;
            double weight = progress * progress * (3 - 2 * progress);
            double expected = baseline[i] + 0.22 * weight;
            actual[i].Should().BeApproximately((float)expected, 2e-5f,
                $"the unchanged Sample's position and value at frame {128 + i} must survive");
        }
    }

    [Test]
    public void AddMixerPreparesTwoSampleReplacementsInOneAtomicTransaction()
    {
        SampleSound a = Sample(FirstId, Enumerable.Repeat(0.11f, 16000).ToArray());
        SampleSound b = Sample(AliasId, Enumerable.Repeat(0.17f, 16000).ToArray());
        SampleSound c = Sample(ThirdId, Enumerable.Repeat(0.29f, 16000).ToArray());
        SampleSound d = Sample(FourthId, Enumerable.Repeat(0.37f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, a), (AliasId, b),
            (ThirdId, c), (FourthId, d));
        FmSynthSound original = MixSound(FirstId, AliasId, resolver);
        FmSynthSound revised = MixSound(ThirdId, FourthId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);

        original.CanAdoptRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);

        float[] actual = new float[4801];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        actual[0].Should().BeApproximately(0.28f, 1e-5f);
        actual[2400].Should().BeApproximately(0.47f, 1e-5f);
        actual[4800].Should().BeApproximately(0.66f, 1e-5f);
    }

    [Test]
    public void AddMixerKeepsOutgoingShortSampleFadeWithoutTruncatingOtherSource()
    {
        SampleSound first = Sample(FirstId,
            Enumerable.Repeat(0.30f, 12000).ToArray());
        SampleSound independent = Sample(AliasId,
            Enumerable.Repeat(0.10f, 12000).ToArray());
        // Reproduces the 27-frame physical test (at this fixture's
        // 48 kHz clock). The old Sample must finish its full fade even
        // though the replacement has gone silent almost immediately.
        SampleSound tiny = Sample(ThirdId,
            Enumerable.Repeat(0.70f, 27).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, independent), (ThirdId, tiny));
        FmSynthSound original = MixSound(FirstId, AliasId, resolver);
        FmSynthSound revised = MixSound(ThirdId, AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        PrepareReplacement(original, revised, held);

        float[] actual = new float[4802];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        actual[0].Should().BeApproximately(0.40f, 1e-5f);
        actual[28].Should().BeGreaterThan(0.39f,
            "the old Sample and independent Source are still sounding");
        actual[2400].Should().BeApproximately(0.25f, 1e-5f);
        actual[4800].Should().BeApproximately(0.10f, 1e-5f,
            "only the independent Source remains after the outgoing fade");
    }

    [Test]
    public void AddMixerAdoptsTwoSampleReplacementsWithSecondFrequencyBasisChangedAtomically()
    {
        const int start = 128, fade = 4800;
        SampleSound a = Sample(FirstId, Enumerable.Repeat(0.11f, 16000).ToArray());
        SampleSound b = Sample(AliasId, Enumerable.Repeat(0.17f, 16000).ToArray());
        SampleSound c = Sample(ThirdId, Enumerable.Repeat(0.29f, 16000).ToArray());
        SampleSound d = Sample(FourthId, Enumerable.Repeat(0.37f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, a), (AliasId, b),
            (ThirdId, c), (FourthId, d));
        FmSynthSound original = MixSound(FirstId, AliasId, resolver);
        FmSynthSound revised = MixSound(ThirdId, FourthId, resolver,
            secondFrequencyHz: FmMultiNode.NeutralFrequencyHz * 2);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);

        // Both inputs must have detached replacements before the Add
        // can change: source 1 chooses C at its normal rate, source 2
        // chooses D at twice its previous frequency. Neither outgoing
        // A nor B is allowed to jump pitch during the first fade.
        original.CanPrepareMultiExpansion(revised, held).Should().BeTrue();
        original.TryPrepareRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);
        float[] actual = new float[fade + 1];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        for (int frame = 0; frame <= fade; frame++)
        {
            double t = (double)frame / fade;
            double weight = t * t * (3 - 2 * t);
            float expected = (float)(0.28 * (1 - weight) + 0.66 * weight);
            actual[frame].Should().BeApproximately(expected, 2e-5f,
                $"both Source replacements must commit atomically at {start + frame}");
        }
    }

    [Test]
    public void NonAddSourceOperatorKeepsSiblingAndCrossfadesOnlyChangedSample()
    {
        const int start = 128, fadeFrames = 4800;
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.50f, 16000).ToArray());
        SampleSound independent = Sample(AliasId,
            Enumerable.Repeat(0.25f, 16000).ToArray());
        SampleSound replacement = Sample(ThirdId,
            Enumerable.Repeat(0.75f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, independent), (ThirdId, replacement));
        FmSynthSound original = MixSound(FirstId, AliasId, resolver,
            FmOperatorKind.Multiply);
        FmSynthSound revised = MixSound(ThirdId, AliasId, resolver,
            FmOperatorKind.Multiply);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        // The old refusal was an obsolete topology restriction. The
        // unchanged sibling continues at 0.25 and multiplies the already
        // source-crossfaded first operand, never a crossfade of the
        // entire compound graph or a reset of its two Sample playheads.
        float[] output = new float[fadeFrames + 1];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < output.Length; i++)
        {
            double t = Math.Clamp((double)i / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            output[i].Should().BeApproximately(
                (float)(0.25 * (0.50 * (1 - w) + 0.75 * w)), 2e-5f,
                $"the unchanged sibling multiplies the local Source fade at {i}");
        }
    }

    [Test]
    public void TwoStageOnePoleCascadeKeepsBothHistoriesForNewSampleSource()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = TwoStageOnePole();
        SampleSound oldSample = Sample(FirstId, Enumerable.Repeat(0.30f, 16000).ToArray());
        SampleSound newSample = Sample(AliasId, Enumerable.Repeat(-0.25f, 16000).ToArray());
        SourceResolver sources = new(FirstId, oldSample, AliasId, newSample);
        FmSynthSound original = FilteredSound(FirstId, sources, filter);
        FmSynthSound revised = FilteredSound(AliasId, sources, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        original.CanAdoptRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);

        // One continuously rendered two-stage cascade receives the
        // precomputed Source-only transition, with no state reset at edit.
        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            if (i < start) { baked[i] = 0.30f; continue; }
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = (float)(0.30 * (1 - w) - 0.25 * w);
        }
        FmSynthSound reference = FilteredReference(filter, baked);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 4e-5f,
                $"both independent OnePole histories remain continuous at {start + i}");
    }

    [Test]
    public void TwoStageOnePoleCascadeRetargetsTwoAddSourcesWithShortIncomingEof()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = TwoStageOnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.40f, 16000).ToArray());
        SampleSound second = Sample(AliasId, Enumerable.Repeat(0.12f, 16000).ToArray());
        SampleSound tiny = Sample(ThirdId, Enumerable.Repeat(0.90f, 48).ToArray());
        SampleSound fourth = Sample(FourthId, Enumerable.Repeat(-0.20f, 16000).ToArray());
        ManySourceResolver sources = new((FirstId, first), (AliasId, second),
            (ThirdId, tiny), (FourthId, fourth));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, sources, filter);
        FmSynthSound revised = FilteredMixSound(ThirdId, FourthId, sources, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        float[] baked = new float[start + fadeFrames + 128];
        for (int i = 0; i < baked.Length; i++)
        {
            if (i < start) { baked[i] = 0.52f; continue; }
            int local = i - start;
            double t = Math.Clamp((double)local / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = (float)(0.40 * (1 - w)
                + (local < 48 ? 0.90 * w : 0)
                + 0.12 * (1 - w) - 0.20 * w);
        }
        FmSynthSound reference = FilteredReference(filter, baked);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 4e-5f,
                $"both Sources atomically fade into an uninterrupted two-stage cascade at {start + i}");
    }

    [Test]
    public void ThreeStageOnePoleCascadeRetainsAllStageHistoriesForSampleReplacement()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = ThreeStageOnePole();
        SampleSound previousSample = Sample(FirstId,
            Enumerable.Repeat(0.35f, 16000).ToArray());
        SampleSound replacementSample = Sample(AliasId,
            Enumerable.Repeat(0.70f, 16000).ToArray());
        SourceResolver sources = new(FirstId, previousSample,
            AliasId, replacementSample);
        FmSynthSound original = FilteredSound(FirstId, sources, filter);
        FmSynthSound revised = FilteredSound(AliasId, sources, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = i < start ? 0.35f : (float)(0.35 * (1 - w) + 0.70 * w);
        }
        FmSynthSound reference = FilteredReference(filter, baked);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 4e-5f,
                $"all three Filter histories remain continuous at {start + i}");
    }

    [Test]
    public void FilteredSingleSourceReplacementRetainsOnePoleHistory()
    {
        FilterInstance filter = OnePole();
        const int start = 128, fadeFrames = 4800;
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.35f, 16000).ToArray());
        SampleSound replacementSample = Sample(AliasId,
            Enumerable.Repeat(-0.40f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, originalSample,
            AliasId, replacementSample);
        FmSynthSound original = FilteredSound(FirstId, resolver, filter);
        FmSynthSound revised = FilteredSound(AliasId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        original.CanAdoptRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);

        // The reference filter is never reconstructed. Its input is a
        // precomputed Sample PCM with the intended source-only fade,
        // so any reset of the running OnePole integrator is detectable.
        float[] referenceInput = new float[10000];
        for (int i = 0; i < referenceInput.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            referenceInput[i] = i < start ? 0.35f
                : (float)(0.35 * (1 - w) - 0.40 * w);
        }
        SampleSound referenceSample = Sample(FirstId, referenceInput);
        SourceResolver referenceResolver = new(FirstId, referenceSample,
            AliasId, referenceSample);
        FmSynthSound reference = FilteredSound(FirstId,
            referenceResolver, filter);
        float[] expected = new float[start + fadeFrames + 32];
        reference.Render(context, reference.CreateState(),
            0, expected.Length, expected);

        float[] actual = new float[fadeFrames + 32];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 3e-5f,
                $"continuous OnePole filter and locally crossfaded Sample at {start + i}");
    }

    [Test]
    public void FilteredShortReplacementKeepsOldSourceFadeAndFilterTail()
    {
        FilterInstance filter = OnePole();
        const int start = 128, fadeFrames = 4800;
        SampleSound originalSample = Sample(FirstId,
            Enumerable.Repeat(0.45f, 12000).ToArray());
        SampleSound shortSample = Sample(AliasId,
            Enumerable.Repeat(0.90f, 48).ToArray());
        SourceResolver resolver = new(FirstId, originalSample,
            AliasId, shortSample);
        FmSynthSound original = FilteredSound(FirstId, resolver, filter);
        FmSynthSound revised = FilteredSound(AliasId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        // 48 frames of the incoming Sample contribute inside the fade;
        // after its EOF the outgoing Sample still supplies the full
        // remaining 100 ms crossfade to the *same* OnePole integrator.
        float[] referenceInput = new float[10000];
        for (int i = 0; i < referenceInput.Length; i++)
        {
            if (i < start) { referenceInput[i] = 0.45f; continue; }
            int local = i - start;
            double t = Math.Clamp((double)local / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            referenceInput[i] = (float)(0.45 * (1 - w)
                + (local < 48 ? 0.90 * w : 0));
        }
        SampleSound referenceSample = Sample(FirstId, referenceInput);
        SourceResolver referenceResolver = new(FirstId, referenceSample,
            AliasId, referenceSample);
        FmSynthSound reference = FilteredSound(FirstId, referenceResolver, filter);
        float[] expected = new float[start + fadeFrames + 144];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);
        float[] actual = new float[fadeFrames + 144];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 3e-5f,
                $"one continuously filtered outgoing fade after the 48-frame EOF at {start + i}");
        actual[fadeFrames - 1].Should().BeGreaterThan(0.0f,
            "Filter history is still present immediately before fade completion");
        actual[^1].Should().BeLessThan(0.001f,
            "the continuing filter must decay after the fade");
    }


    [Test]
    public void FilteredAddMixerRetargetsOneSourceWithContinuousOnePole()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.35f, 16000).ToArray());
        float[] siblingPcm = Enumerable.Range(0, 16000)
            .Select(i => (float)(0.08 * Math.Sin(i * 0.053))).ToArray();
        SampleSound sibling = Sample(AliasId, siblingPcm);
        SampleSound replacement = Sample(ThirdId,
            Enumerable.Repeat(-0.40f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, sibling), (ThirdId, replacement));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver, filter);
        FmSynthSound revised = FilteredMixSound(ThirdId, AliasId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        original.CanAdoptRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);

        // This reference has just one Filter and one continuous Sample
        // with the expected per-frame mix already baked into its input.
        // It detects any hidden reset of the filter's integrator or of
        // the independent sibling's physical Sample playhead.
        float[] referencePcm = new float[10000];
        for (int i = 0; i < referencePcm.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            referencePcm[i] = (float)(0.35 * (1 - w) - 0.40 * w)
                + siblingPcm[i];
        }
        FmSynthSound reference = FilteredReference(filter, referencePcm);
        float[] expected = new float[start + fadeFrames + 64];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);

        float[] actual = new float[fadeFrames + 64];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 3e-5f,
                $"the sibling Sample and one unbroken OnePole history at frame {start + i}");
    }

    [Test]
    public void FilteredAddMixerPreparesBothSourceReplacementsAtomically()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.11f, 16000).ToArray());
        SampleSound second = Sample(AliasId, Enumerable.Repeat(0.17f, 16000).ToArray());
        SampleSound third = Sample(ThirdId, Enumerable.Repeat(0.29f, 16000).ToArray());
        SampleSound fourth = Sample(FourthId, Enumerable.Repeat(0.37f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first), (AliasId, second),
            (ThirdId, third), (FourthId, fourth));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver, filter);
        FmSynthSound revised = FilteredMixSound(ThirdId, FourthId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        original.CanAdoptRunningState(revised, held).Should().BeFalse();
        PrepareReplacement(original, revised, held);

        float[] referencePcm = new float[10000];
        for (int i = 0; i < referencePcm.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            referencePcm[i] = i < start ? 0.28f
                : (float)(0.11 * (1 - w) + 0.29 * w)
                    + (float)(0.17 * (1 - w) + 0.37 * w);
        }
        FmSynthSound reference = FilteredReference(filter, referencePcm);
        float[] expected = new float[start + fadeFrames + 64];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);
        float[] actual = new float[fadeFrames + 64];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 3e-5f,
                $"both worker-prepared Sources pass through the same continuous filter at {start + i}");
    }

    [Test]
    public void FilteredAddMixerShortReplacementCannotCutOutgoingFadeOrFilterTail()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.45f, 12000).ToArray());
        // A finite sibling also ends during the outgoing fade.
        SampleSound sibling = Sample(AliasId, Enumerable.Repeat(0.10f, 1024).ToArray());
        SampleSound tiny = Sample(ThirdId, Enumerable.Repeat(0.90f, 48).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, sibling), (ThirdId, tiny));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver, filter);
        FmSynthSound revised = FilteredMixSound(ThirdId, AliasId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        float[] referencePcm = new float[10000];
        for (int i = 0; i < referencePcm.Length; i++)
        {
            int local = i - start;
            double t = Math.Clamp((double)local / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            referencePcm[i] = (float)(i < start ? 0.45
                : 0.45 * (1 - w) + (local < 48 ? 0.90 * w : 0))
                + (i < 1024 ? 0.10f : 0.0f);
        }
        FmSynthSound reference = FilteredReference(filter, referencePcm);
        float[] expected = new float[start + fadeFrames + 144];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);
        float[] actual = new float[fadeFrames + 144];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 3e-5f,
                $"the 48-frame EOF, sibling EOF and downstream filter history at {start + i}");
        actual[2400].Should().BeGreaterThan(0.10f,
            "the old Sample must carry the transition long after both incoming and sibling EOFs");
        actual[fadeFrames + 100].Should().BeLessThan(0.005f,
            "the unchanged OnePole continues its natural decay");
    }

    [Test]
    public void FilteredAddMixerCanReviveSourceAfterShortSampleEofWithoutResettingOnePole()
    {
        const int start = 128, fadeFrames = 4800;
        const int secondStart = start + fadeFrames + 1;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.30f, 18000).ToArray());
        SampleSound sibling = Sample(AliasId, Enumerable.Repeat(0.10f, 18000).ToArray());
        SampleSound tiny = Sample(ThirdId, Enumerable.Repeat(0.70f, 48).ToArray());
        SampleSound revived = Sample(FourthId, Enumerable.Repeat(-0.20f, 18000).ToArray());
        ManySourceResolver resolver = new((FirstId, first), (AliasId, sibling),
            (ThirdId, tiny), (FourthId, revived));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver, filter);
        FmSynthSound shortened = FilteredMixSound(ThirdId, AliasId, resolver, filter);
        FmSynthSound restored = FilteredMixSound(FourthId, AliasId, resolver, filter);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, shortened, held);

        float[] firstTransition = new float[fadeFrames + 1];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        shortened.Render(context, held, start, firstTransition.Length,
            firstTransition);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);
        PrepareReplacement(shortened, restored, held);

        float[] secondTransition = new float[fadeFrames + 64];
        allocation = GC.GetAllocatedBytesForCurrentThread();
        restored.Render(context, held, secondStart, secondTransition.Length,
            secondTransition);
        (GC.GetAllocatedBytesForCurrentThread() - allocation).Should().Be(0);

        // The short Source has already reached EOF by the second edit.
        // Its replacement must still start at a fresh frame zero while
        // the sibling and the shared downstream filter remain continuous.
        float[] referencePcm = new float[secondStart + secondTransition.Length];
        for (int i = 0; i < referencePcm.Length; i++)
        {
            double sound = i < start ? 0.30 : 0;
            if (i >= start && i < secondStart)
            {
                int local = i - start;
                double t = Math.Clamp((double)local / fadeFrames, 0, 1);
                double w = t * t * (3 - 2 * t);
                sound = 0.30 * (1 - w) + (local < 48 ? 0.70 * w : 0);
            }
            if (i >= secondStart)
            {
                int local = i - secondStart;
                double t = Math.Clamp((double)local / fadeFrames, 0, 1);
                double w = t * t * (3 - 2 * t);
                sound = -0.20 * w;
            }
            referencePcm[i] = (float)sound + 0.10f;
        }
        FmSynthSound reference = FilteredReference(filter, referencePcm);
        float[] expected = new float[referencePcm.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length,
            expected);
        for (int i = 0; i < firstTransition.Length; i++)
            firstTransition[i].Should().BeApproximately(expected[start + i], 3e-5f);
        for (int i = 0; i < secondTransition.Length; i++)
            secondTransition[i].Should().BeApproximately(
                expected[secondStart + i], 3e-5f,
                $"returning to another Sample at frame {secondStart + i} preserves one Filter");
    }

    [Test]
    public void FilteredMultiplyMixerCrossfadesSampleAtItsSourceBeforeNonlinearOperator()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.35f, 16000).ToArray());
        SampleSound sibling = Sample(AliasId, Enumerable.Repeat(0.10f, 16000).ToArray());
        SampleSound replacement = Sample(ThirdId,
            Enumerable.Repeat(0.55f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first),
            (AliasId, sibling), (ThirdId, replacement));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver,
            filter, FmOperatorKind.Multiply);
        FmSynthSound revised = FilteredMixSound(ThirdId, AliasId, resolver,
            filter, FmOperatorKind.Multiply);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        // Crossfade the SOURCE, then multiply by the independently
        // continuing sibling, then run the same Filter history.
        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = (float)(0.1 * (i < start
                ? 0.35 : 0.35 * (1 - w) + 0.55 * w));
        }
        FmSynthSound reference = FilteredReference(filter, baked);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 4e-5f,
                $"the Sample blend precedes Multiply and the continuous Filter at {start + i}");
    }

    [Test]
    public void SharedSampleFanoutThroughBiquadAndMultipleFilterNodesPreservesDspHistories()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance biquad = FilterWithKind((ObjectId)9940U, FilterKind.BiquadLowPass);
        FilterInstance onePole = OnePole();
        FilterInstance threePoles = ThreeStageOnePole();
        SampleSound oldSample = Sample(FirstId, Enumerable.Repeat(0.30f, 16000).ToArray());
        SampleSound nextSample = Sample(AliasId, Enumerable.Repeat(-0.20f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, oldSample, AliasId, nextSample);
        FmSynthSound original = SharedSampleFanoutSound(
            FirstId, resolver, biquad, onePole, threePoles);
        FmSynthSound revised = SharedSampleFanoutSound(
            AliasId, resolver, biquad, onePole, threePoles);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        // The reference routes a single baked Source through both
        // Filter branches, their joined Add, then the final Multiply.
        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = i < start ? 0.30f
                : (float)(0.30 * (1 - w) - 0.20 * w);
        }
        SampleSound referenceSample = Sample(FirstId, baked);
        SourceResolver referenceResolver = new(FirstId, referenceSample,
            AliasId, referenceSample);
        FmSynthSound reference = SharedSampleFanoutSound(
            FirstId, referenceResolver, biquad, onePole, threePoles);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 5e-5f,
                $"the fan-out, Biquad, serial OnePole, triple cascade, and operators at {start+i}");
    }

    [Test]
    public void FilterWithConnectedCutoffModulationRetainsHistoryAcrossSampleReplacement()
    {
        const int start = 128, fadeFrames = 4800;
        FilterInstance filter = FilterWithKind((ObjectId)9941U, FilterKind.BiquadLowPass);
        FmFilterParameterConnection cutoff = new(1, FilterKind.BiquadLowPass,
            filter.GetStageGenerationForPersistence(1), "CutoffHz", 2);

        SampleSound previous = Sample(FirstId, Enumerable.Repeat(0.40f, 16000).ToArray());
        SampleSound replacement = Sample(AliasId, Enumerable.Repeat(-0.15f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, previous, AliasId, replacement);

        FmSynthSound Build(ObjectId id, SourceResolver sounds) => new(new FmSynthGraph([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmConstantNode(2, 1200),
            new FmFilterNode(3, 1, filter, [cutoff], tailSeconds: 0.003),
        ], 3), sounds, soundResolver: sounds,
            enableLiveMultiExpansion: true);

        FmSynthSound original = Build(FirstId, resolver);
        FmSynthSound revised = Build(AliasId, resolver);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        PrepareReplacement(original, revised, held);

        float[] baked = new float[start + fadeFrames + 96];
        for (int i = 0; i < baked.Length; i++)
        {
            double t = Math.Clamp((double)(i - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[i] = i < start ? 0.40f
                : (float)(0.40 * (1 - w) - 0.15 * w);
        }
        SampleSound bakedSample = Sample(FirstId, baked);
        SourceResolver referenceResolver = new(FirstId, bakedSample,
            AliasId, bakedSample);
        FmSynthSound reference = Build(FirstId, referenceResolver);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[baked.Length - start];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 5e-5f,
                $"unchanged numeric modulation and Biquad state at frame {start + i}");
    }

    [Test]
    public void IncompatibleFilterStageAlongsideSourceIdChangeDeclinesWholeEdit()
    {
        FilterInstance originalFilter = FilterWithKind(
            (ObjectId)9942U, FilterKind.BiquadLowPass);
        FilterInstance incompatible = FilterWithKind(
            (ObjectId)9942U, FilterKind.BiquadHighPass);
        SampleSound first = Sample(FirstId,
            Enumerable.Repeat(0.30f, 16000).ToArray());
        SampleSound replacement = Sample(AliasId,
            Enumerable.Repeat(0.80f, 16000).ToArray());
        SourceResolver resolver = new(FirstId, first, AliasId, replacement);
        FmSynthSound original = FilteredSound(FirstId, resolver, originalFilter);
        FmSynthSound revised = FilteredSound(AliasId, resolver, incompatible);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState uninterrupted = original.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        original.Render(context, uninterrupted, 0, 128, new float[128]);

        original.CanPrepareMultiExpansion(revised, held).Should().BeFalse();
        original.RequestMultiExpansionPreparation(revised, held).Should().BeFalse();
        original.TryPrepareRunningState(revised, held).Should().BeFalse();
        float[] actual = new float[320], expected = new float[320];
        original.Render(context, held, 128, actual.Length, actual);
        original.Render(context, uninterrupted, 128, expected.Length, expected);
        actual.Should().Equal(expected,
            "Source replacement must not bypass the global Filter stage compatibility veto");
    }

    [Test]
    public void FilteredAddMixerAdoptsSourceIdAndFrequencyTogetherWithoutResettingOnePole()
    {
        const int start = 128, fade = 4800;
        FilterInstance filter = OnePole();
        SampleSound first = Sample(FirstId, Enumerable.Repeat(0.11f, 16000).ToArray());
        SampleSound sibling = Sample(AliasId, Enumerable.Repeat(0.17f, 16000).ToArray());
        SampleSound third = Sample(ThirdId, Enumerable.Repeat(0.29f, 16000).ToArray());
        SampleSound fourth = Sample(FourthId, Enumerable.Repeat(0.37f, 16000).ToArray());
        ManySourceResolver resolver = new((FirstId, first), (AliasId, sibling),
            (ThirdId, third), (FourthId, fourth));
        FmSynthSound original = FilteredMixSound(FirstId, AliasId, resolver, filter);
        FmSynthSound revised = FilteredMixSound(ThirdId, FourthId, resolver,
            filter, secondFrequencyHz: FmMultiNode.NeutralFrequencyHz * 2);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);

        original.CanPrepareMultiExpansion(revised, held).Should().BeTrue();
        PrepareReplacement(original, revised, held);
        float[] incomingPcm = new float[start + fade + 64];
        for (int frame = 0; frame < incomingPcm.Length; frame++)
        {
            double t = Math.Clamp((double)(frame - start) / fade, 0, 1);
            double w = t * t * (3 - 2 * t);
            incomingPcm[frame] = (float)(0.28 * (1 - w) + 0.66 * w);
        }
        FmSynthSound oracle = FilteredReference(filter, incomingPcm);
        float[] expected = new float[incomingPcm.Length];
        oracle.Render(context, oracle.CreateState(), 0, expected.Length, expected);
        float[] actual = new float[incomingPcm.Length - start];
        long before = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        for (int frame = 0; frame < actual.Length; frame++)
            actual[frame].Should().BeApproximately(expected[start + frame], 3e-5f,
                $"both Sources must feed the same uninterrupted OnePole at {start + frame}");
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


    private static FilterInstance FilterWithKind(ObjectId id, FilterKind kind)
        => new(new FilterDefinition(id, "Sample migration filter", [
            new FilterStageDefinition(1, "stage", kind),
        ]));

    private static FmSynthSound SharedSampleFanoutSound(ObjectId source,
        SourceResolver resolver, FilterInstance biquad,
        FilterInstance onePole, FilterInstance triple)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, source, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, biquad, tailSeconds: 0.003),
            new FmFilterNode(3, 2, onePole, tailSeconds: 0.003),
            new FmFilterNode(4, 1, triple, tailSeconds: 0.003),
            new FmOperatorNode(5, FmOperatorKind.Add, [3, 4]),
            new FmConstantNode(6, 0.5),
            new FmOperatorNode(7, FmOperatorKind.Multiply, [5, 6]),
        ], 7), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private static FilterInstance ThreeStageOnePole()
    {
        FilterDefinition definition = new((ObjectId)9922U, "Unadmitted third OnePole", [
            new FilterStageDefinition(1, "first", FilterKind.OnePole),
            new FilterStageDefinition(2, "second", FilterKind.OnePole),
            new FilterStageDefinition(3, "third", FilterKind.OnePole),
        ]);
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(800));
        filter.SetStageParameter(2, "CutoffHz",
            FilterParameterValue.FromNumber(1800));
        filter.SetStageParameter(3, "CutoffHz",
            FilterParameterValue.FromNumber(2500));
        return filter;
    }

    private static FilterInstance TwoStageOnePole()
    {
        FilterDefinition definition = new((ObjectId)9921U, "Two-stage Source cascade", [
            new FilterStageDefinition(1, "first", FilterKind.OnePole),
            new FilterStageDefinition(2, "second", FilterKind.OnePole),
        ]);
        FilterInstance instance = new(definition);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(800));
        instance.SetStageParameter(2, "CutoffHz",
            FilterParameterValue.FromNumber(1800));
        return instance;
    }

    private static FilterInstance OnePole()
    {
        FilterDefinition definition = new((ObjectId)9920U, "Source replacement pole");
        FilterInstance instance = new(definition);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        return instance;
    }

    private static FmSynthSound FilteredSound(ObjectId source,
        SourceResolver resolver, FilterInstance filter)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, source, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
        ], 2), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);


    private static FmSynthSound FilteredMixSound(ObjectId firstId,
        ObjectId secondId, ManySourceResolver resolver, FilterInstance filter,
        FmOperatorKind operation = FmOperatorKind.Add,
        double? secondFrequencyHz = null)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, firstId, FmMultiNode.NeutralFrequencyHz),
            new FmSoundSourceNode(2, secondId,
                secondFrequencyHz ?? FmMultiNode.NeutralFrequencyHz),
            new FmOperatorNode(3, operation, [1, 2]),
            new FmFilterNode(4, 3, filter, tailSeconds: 0.003),
        ], 4), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private static FmSynthSound FilteredReference(FilterInstance filter,
        float[] precomputedInput)
    {
        SampleSound referenceSample = Sample(FirstId, precomputedInput);
        SourceResolver resolver = new(FirstId, referenceSample,
            AliasId, referenceSample);
        return FilteredSound(FirstId, resolver, filter);
    }

    private static FmSynthSound MixSound(ObjectId firstId, ObjectId secondId,
        ManySourceResolver resolver, FmOperatorKind operation = FmOperatorKind.Add,
        double? secondFrequencyHz = null)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, firstId, FmMultiNode.NeutralFrequencyHz),
            new FmSoundSourceNode(2, secondId,
                secondFrequencyHz ?? FmMultiNode.NeutralFrequencyHz),
            new FmOperatorNode(3, operation, [1, 2]),
        ], 3), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private sealed class ManySourceResolver(params (ObjectId id, SampleSound sound)[] items)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, SampleSound> _sources =
            items.ToDictionary(pair => pair.id, pair => pair.sound);

        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = _sources.TryGetValue(id, out SampleSound? result)
                ? result : null;
            return sound is not null;
        }

        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }

    private sealed class OneCurve(ObjectId id, NodeEnvelopeCurve value)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = requested == id ? value : null;
            return curve is not null;
        }
    }

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
