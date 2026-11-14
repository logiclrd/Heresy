using System;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
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
public sealed class FmSynthLiveFilteredMultiOutputTests
{
    private static readonly ObjectId SampleId = (ObjectId)9470U;

    [TestCase(FmOperatorKind.Multiply, 0.87, 0.16)]
    [TestCase(FmOperatorKind.Add, 0.67, -0.21)]
    public void FrozenFilteredSampleMultiChildFadesOuterGainWithoutReinitializingHistory(
        FmOperatorKind operation, double beforeGain, double afterGain)
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        AssertSmoothEdit(Sound(sample, filter, beforeGain, operation),
            Sound(sample, filter, afterGain, operation),
            operation == FmOperatorKind.Add);
    }

    [Test]
    public void FrozenFilteredSampleMultiChildFadesOuterOperatorMode()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        AssertSmoothEdit(Sound(sample, filter, 0.31, FmOperatorKind.Multiply),
            Sound(sample, filter, 0.31, FmOperatorKind.Add), additive: true);
    }

    [Test]
    public void FrozenModulatedTwoStageMultiFilterKeepsOuterFadeDuringInnerRetune()
    {
        const int firstEdit = 128, secondEdit = 192, fade = 4800;
        FilterInstance oldFilter = TwoStageModulatedFilter(revised: false);
        FilterInstance newFilter = TwoStageModulatedFilter(revised: true);
        FmSynthSound Build(FilterInstance filter, double gain)
        {
            FmFilterParameterConnection cutoff = new(1, FilterKind.OnePole,
                filter.GetStageGenerationForPersistence(1), "CutoffHz", 2);
            return new(new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 2600),
                new FmConstantNode(2, 1500),
                new FmFilterNode(3, 1, filter, [cutoff], tailSeconds: 0.003),
                new FmMultiNode(4, 3, voiceCount: 3, detune: 0,
                    randomizePhase: false, gainCompensation: true),
                new FmConstantNode(5, gain),
                new FmOperatorNode(6, FmOperatorKind.Multiply, [4, 5]),
            ], 6), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        }

        FmSynthSound initial = Build(oldFilter, 0.85);
        FmSynthSound gainEdited = Build(oldFilter, 0.20);
        FmSynthSound filterEdited = Build(newFilter, 0.20);

        // This independent reference has unity gain at every frame.
        // Its inner Multi, connected cutoff, and ongoing SVF history are
        // identical to those in the primary voice; only the audible final
        // gain is different, so the oracle can track the moving waveform.
        FmSynthSound rawInitial = Build(oldFilter, 1.0);
        FmSynthSound rawEdited = Build(newFilter, 1.0);
        RenderContext context = Mono();
        SoundState held = initial.CreateState();
        SoundState raw = rawInitial.CreateState();
        initial.Render(context, held, 0, firstEdit, new float[firstEdit]);
        rawInitial.Render(context, raw, 0, firstEdit, new float[firstEdit]);

        initial.CanAdoptRunningState(gainEdited, held).Should().BeTrue();
        initial.TryPrepareRunningState(gainEdited, held).Should().BeTrue();
        initial.CommitPreparedRunningState(gainEdited, held);

        float[] initialRamp = new float[secondEdit - firstEdit];
        float[] rawFirst = new float[initialRamp.Length];
        rawInitial.Render(context, raw, firstEdit, rawFirst.Length, rawFirst);
        gainEdited.Render(context, held, firstEdit, initialRamp.Length, initialRamp);

        for (int i = 0; i < initialRamp.Length; i++)
        {
            double t = (double)i / fade;
            double w = t * t * (3 - 2 * t);
            initialRamp[i].Should().BeApproximately(
                (float)(rawFirst[i] * (0.85 * (1 - w) + 0.20 * w)),
                4e-5f,
                "a completely unchanged connected two-stage Filter inside"
                + " Multi must not disqualify outer moving-gain smoothing");
        }

        // Retune the UNCONNECTED SVF stage while the outer gain is still
        // fading. The OnePole's wired cutoff continues at its existing
        // modulation value. Neither inner Filter history nor the previous
        // output gain ramp may be restarted.
        gainEdited.CanAdoptRunningState(filterEdited, held).Should().BeTrue();
        rawInitial.CanAdoptRunningState(rawEdited, raw).Should().BeTrue();
        gainEdited.TryPrepareRunningState(filterEdited, held).Should().BeTrue();
        rawInitial.TryPrepareRunningState(rawEdited, raw).Should().BeTrue();
        gainEdited.CommitPreparedRunningState(filterEdited, held);
        rawInitial.CommitPreparedRunningState(rawEdited, raw);

        float[] actual = new float[fade + 48];
        float[] expectedRaw = new float[actual.Length];
        rawEdited.Render(context, raw, secondEdit, expectedRaw.Length, expectedRaw);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        filterEdited.Render(context, held, secondEdit, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)(secondEdit + i - firstEdit) / fade, 0, 1);
            double w = t * t * (3 - 2 * t);
            float expected = (float)(expectedRaw[i] *
                (0.85 * (1 - w) + 0.20 * w));
            actual[i].Should().BeApproximately(expected, 7e-5f,
                $"the parent gain clock and Multi child Filter histories"
                + $" must continue together at PCM {secondEdit + i}");
        }
    }


    [Test]
    public void SimultaneousSourceRateFilterRetuneAndOuterGainInsideMultiPreservesMovingAudio()
    {
        const int editFrame = 128, fadeFrames = 4800;
        SampleSound sample = Sample(16000);
        FilterInstance oldFilter = OnePole();
        FilterInstance revisedFilter = OnePole();
        revisedFilter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(3600));

        FmSynthSound Build(double sourceFrequency, FilterInstance filter,
            double gain)
        {
            SampleResolver resolver = new(sample);
            return new(new FmSynthGraph([
                // The complete Multi child contains a running Sample readhead,
                // its own OnePole integrator and a phase-continuous sibling.
                new FmSoundSourceNode(1, SampleId, sourceFrequency),
                new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
                new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 709,
                    minimum: -0.08, maximum: 0.08),
                new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
                new FmMultiNode(5, 4, voiceCount: 3, detune: 0,
                    randomizePhase: false, gainCompensation: true),
                new FmConstantNode(6, gain),
                new FmOperatorNode(7, FmOperatorKind.Multiply, [5, 6]),
            ], 7), resolver, soundResolver: resolver,
                enableLiveMultiExpansion: true);
        }

        double neutral = FmMultiNode.NeutralFrequencyHz;
        FmSynthSound original = Build(neutral, oldFilter, 0.85);
        FmSynthSound combined = Build(neutral * 1.35, revisedFilter, 0.20);
        FmSynthSound unityOriginal = Build(neutral, oldFilter, 1.0);
        FmSynthSound unityRevised = Build(neutral * 1.35, revisedFilter, 1.0);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState raw = unityOriginal.CreateState();
        original.Render(context, held, 0, editFrame, new float[editFrame]);
        unityOriginal.Render(context, raw, 0, editFrame, new float[editFrame]);

        original.CanAdoptRunningState(combined, held).Should().BeTrue();
        unityOriginal.CanAdoptRunningState(unityRevised, raw).Should().BeTrue();
        original.TryPrepareRunningState(combined, held).Should().BeTrue();
        unityOriginal.TryPrepareRunningState(unityRevised, raw).Should().BeTrue();
        original.CommitPreparedRunningState(combined, held);
        unityOriginal.CommitPreparedRunningState(unityRevised, raw);

        // The unchanged-unity-gain reference has the exact same forward
        // integrated Sample pitch, independent oscillator phases and three
        // independently retuning OnePole histories. Only the parent's
        // 100-ms moving gain differs; it must not be lost just because
        // the eligible child controls also changed in this publication.
        float[] reference = new float[fadeFrames + 96];
        unityRevised.Render(context, raw, editFrame, reference.Length, reference);
        float[] actual = new float[reference.Length];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        combined.Render(context, held, editFrame, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0,
            "simultaneous Multi-child Sample/Filter and parent gain migration"
            + " cannot allocate or restart child invocation state in PCM");

        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            actual[i].Should().BeApproximately(
                (float)(reference[i] * (0.85 * (1 - w) + 0.20 * w)),
                8e-5f,
                $"source pitch, three Filter histories, oscillator phases"
                + $" and parent moving gain must agree at frame {editFrame + i}");
        }
    }

    [Test]
    public void BoundFirInsideMultiWaitsForWorkerThenRetunesWithoutInterruptingOuterGain()
    {
        const int firstEdit = 128, secondEdit = 192;
        const int delayWhileUnprepared = 16, fadeFrames = 4800;
        FilterInstance originalFilter = BoundFirBandPass(revised: false);
        FilterInstance revisedFilter = BoundFirBandPass(revised: true);

        FmSynthSound Build(FilterInstance filter, double gain)
        {
            // Only LowCutoffHz is FM-bound. The edited HighCutoffHz remains
            // independently authored, so its replacement FIR design must
            // use this invocation's most recently accepted low cutoff.
            FmFilterParameterConnection low = new(1, FilterKind.FirBandPass,
                filter.GetStageGenerationForPersistence(1), "LowCutoffHz", 2);
            return new(new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 5500),
                new FmConstantNode(2, 950),
                new FmFilterNode(3, 1, filter, [low], tailSeconds: 0.004),
                new FmMultiNode(4, 3, voiceCount: 3, detune: 0,
                    randomizePhase: false, gainCompensation: true),
                new FmConstantNode(5, gain),
                new FmOperatorNode(6, FmOperatorKind.Multiply, [4, 5]),
            ], 6), new EmptyEnvelopes(),
                preparedChildFilterFactory: new PreparedToneFilterStageFactory(48000),
                enableLiveMultiExpansion: true);
        }

        FmSynthSound original = Build(originalFilter, 0.85);
        FmSynthSound gainEdited = Build(originalFilter, 0.20);
        FmSynthSound firEdited = Build(revisedFilter, 0.20);
        FmSynthSound rawOriginal = Build(originalFilter, 1.0);
        FmSynthSound rawEdited = Build(revisedFilter, 1.0);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState reference = rawOriginal.CreateState();
        original.Render(context, held, 0, firstEdit, new float[firstEdit]);
        rawOriginal.Render(context, reference, 0, firstEdit,
            new float[firstEdit]);

        original.TryPrepareRunningState(gainEdited, held).Should().BeTrue();
        original.CommitPreparedRunningState(gainEdited, held);
        gainEdited.Render(context, held, firstEdit,
            secondEdit - firstEdit, new float[secondEdit - firstEdit]);
        rawOriginal.Render(context, reference, firstEdit,
            secondEdit - firstEdit, new float[secondEdit - firstEdit]);

        // FIR reconfiguration is a nonstructural numeric edit but needs
        // a distinct worker kernel for EACH Multi constituent. A request
        // for all three must not mutate any live voice before publication.
        gainEdited.CanAdoptRunningState(firEdited, held).Should().BeTrue();
        rawOriginal.CanAdoptRunningState(rawEdited, reference).Should().BeTrue();
        gainEdited.TryPrepareRunningState(firEdited, held).Should().BeFalse();
        rawOriginal.TryPrepareRunningState(rawEdited, reference).Should().BeFalse();
        gainEdited.RequestBoundLiveFirPreparation(firEdited, held).Should().BeTrue();
        rawOriginal.RequestBoundLiveFirPreparation(rawEdited, reference)
            .Should().BeTrue();

        float[] heldBeforeReady = new float[delayWhileUnprepared];
        float[] rawBeforeReady = new float[delayWhileUnprepared];
        gainEdited.Render(context, held, secondEdit,
            heldBeforeReady.Length, heldBeforeReady);
        rawOriginal.Render(context, reference, secondEdit,
            rawBeforeReady.Length, rawBeforeReady);
        for (int i = 0; i < heldBeforeReady.Length; i++)
        {
            double t = (double)(secondEdit + i - firstEdit) / fadeFrames;
            double w = t * t * (3 - 2 * t);
            float expected = (float)(rawBeforeReady[i] *
                (0.85 * (1 - w) + 0.20 * w));
            heldBeforeReady[i].Should().BeApproximately(expected, 7e-5f,
                "while any FIR plan is unavailable, all three child"
                + " kernels and the parent gain clock must keep running");
        }

        using FirPreparationScheduler worker = new();
        gainEdited.QueuePendingFirControls(held, worker);
        rawOriginal.QueuePendingFirControls(reference, worker);
        SpinWait.SpinUntil(
            () => worker.CompletedPreparationCount >= 6,
            TimeSpan.FromSeconds(5)).Should().BeTrue(
                "each of three Multi children in each voice needs an"
                + " independent prepared FIR plan");
        gainEdited.AdoptPreparedFirControls(held);
        rawOriginal.AdoptPreparedFirControls(reference);
        gainEdited.TryPrepareRunningState(firEdited, held).Should().BeTrue();
        rawOriginal.TryPrepareRunningState(rawEdited, reference).Should().BeTrue();
        gainEdited.CommitPreparedRunningState(firEdited, held);
        rawOriginal.CommitPreparedRunningState(rawEdited, reference);
        worker.Fault.Should().BeNull();

        int resumed = secondEdit + delayWhileUnprepared;
        float[] actual = new float[fadeFrames + 48];
        float[] raw = new float[actual.Length];
        rawEdited.Render(context, reference, resumed, raw.Length, raw);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        firEdited.Render(context, held, resumed, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)(resumed + i - firstEdit)
                / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            float expected = (float)(raw[i] *
                (0.85 * (1 - w) + 0.20 * w));
            actual[i].Should().BeApproximately(expected, 8e-5f,
                $"the bound-FIR child histories and outer output gain must"
                + $" advance together at PCM frame {resumed + i}");
        }
    }

    [Test]
    public void RepeatedOuterEditsAnchorAtBlendedOutputWithFilterHistoryContinuing()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        FmSynthSound first = Sound(sample, filter, 0.83);
        FmSynthSound second = Sound(sample, filter, 0.17);
        FmSynthSound third = Sound(sample, filter, 0.17, FmOperatorKind.Add);
        RenderContext context = Mono();
        SoundState held = first.CreateState();
        first.Render(context, held, 0, 128, new float[128]);

        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] partial = new float[19];
        second.Render(context, held, 128, partial.Length, partial);

        second.TryPrepareRunningState(third, held).Should().BeTrue();
        second.CommitPreparedRunningState(third, held);
        float[] reference = new float[243], output = new float[96];
        third.Render(context, third.CreateState(), 0, reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        third.Render(context, held, 147, output.Length, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "neither Sample/Filter history nor Multi children may allocate in PCM");
        AssertInterruptedAffineFade(partial[^1], reference, output,
            147, 0.83, 0.17, 0.17);
    }

    [Test]
    public void FilterTailAndOriginalSampleEofSurviveParentFade()
    {
        SampleSound sample = Sample(400);
        FilterInstance filter = OnePole();
        FmSynthSound before = Sound(sample, filter, 0.85);
        FmSynthSound after = Sound(sample, filter, 0.19);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);

        // One branch ends at the original Sample EOF, the serial
        // OnePole tail decays, and the parallel Oscillator continues.
        // All three independent Multi constituent histories must match
        // a fresh reference beyond the outer 100 ms moving-affine transition.
        float[] reference = new float[128 + AdditiveFadeFrames + 64],
            actual = new float[AdditiveFadeFrames + 64];
        after.Render(context, after.CreateState(), 0,
            reference.Length, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        for (int i = AdditiveFadeFrames; i < actual.Length; i++)
            actual[i].Should().BeApproximately(reference[128 + i], 4e-6f,
                "the unchanged Sample readhead and Filter tail must never"
                + " be restarted, trimmed or re-filtered by an outer gain edit");
    }

    [Test]
    public void NormalPlaybackKeepsImmediateFilteredMultiOutputControls()
    {
        SampleSound sample = Sample(1800);
        FilterInstance filter = OnePole();
        FmSynthSound before = Sound(sample, filter, 0.85, audition: false);
        FmSynthSound after = Sound(sample, filter, 0.15, audition: false);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        float[] reference = new float[129], actual = new float[1];
        after.Render(context, after.CreateState(), 0, reference.Length, reference);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        after.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(reference[128], 4e-6f,
            "ordinary playback/export must not get audition-only fades");
    }

    private const int AdditiveFadeFrames = 4800;

    private static void AssertSmoothEdit(FmSynthSound before,
        FmSynthSound after, bool additive = false)
    {
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        SoundState fresh = after.CreateState();
        int length = AdditiveFadeFrames + 32;
        float[] previous = new float[128], actual = new float[length],
            reference = new float[128 + length];
        before.Render(context, held, 0, previous.Length, previous);
        after.Render(context, fresh, 0, reference.Length, reference);
        float[] oldReference = new float[128 + length];
        SoundState previousState = before.CreateState();
        before.Render(context, previousState, 0,
            oldReference.Length, oldReference);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "the already-bound Multi children and histories must not"
            + " allocate or be reconstructed in PCM");
        AssertMovingAffineFade(oldReference, reference, actual, 128);
    }

    private static void AssertMovingAffineFade(float[] oldReference,
        float[] newReference, float[] actual, int start)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / AdditiveFadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            actual[i].Should().BeApproximately(
                (float)(oldReference[start + i] * (1 - w)
                    + newReference[start + i] * w), 4e-6f,
                "Add changes must retain the moving original signal while"
                + " smoothing its DC offset and gain over 100 ms");
        }
    }

    private static void AssertInterruptedAffineFade(float lastAudible,
        float[] finalReference, float[] actual, int start,
        double firstScale, double middleScale, double revisedAddConstant)
    {
        // An interrupted moving Multiply ramp becomes the next ramp's
        // starting gain: don't hold an audio sample or reset DSP history.
        double before = Math.Clamp((double)(start - 1 - 128)
            / AdditiveFadeFrames, 0, 1);
        double previousWeight = before * before * (3 - 2 * before);
        double previousScale = firstScale * (1 - previousWeight)
            + middleScale * previousWeight;
        double previousSignal = finalReference[start - 1] - revisedAddConstant;
        lastAudible.Should().BeApproximately(
            (float)(previousScale * previousSignal), 6e-6f);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((double)i / AdditiveFadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            double movingSignal = finalReference[start + i] - revisedAddConstant;
            double expected = movingSignal * (previousScale * (1 - w) + w)
                + revisedAddConstant * w;
            actual[i].Should().BeApproximately((float)expected, 6e-6f,
                "the second edit must inherit the previous moving gain"
                + " and continue original Multi audio without a hold");
        }
    }

    private static FmSynthSound Sound(SampleSound sample, FilterInstance filter,
        double gain, FmOperatorKind operation = FmOperatorKind.Multiply,
        bool audition = true)
    {
        SampleResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.002),
            new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 390),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
            new FmMultiNode(5, 4, voiceCount: 3, detune: 17,
                randomizePhase: false, gainCompensation: true),
            new FmConstantNode(6, gain),
            new FmOperatorNode(7, operation, [5, 6]),
        ], 7), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: audition);
    }

    private static FilterInstance BoundFirBandPass(bool revised)
    {
        FilterDefinition definition = new((ObjectId)9473U,
            "C3 bound FIR child", [
                new FilterStageDefinition(1, "Band", FilterKind.FirBandPass),
            ]);
        FilterInstance filter = new(definition);
        filter.SetStageParameters(1, [
            new("LowCutoffHz", FilterParameterValue.FromNumber(950)),
            new("HighCutoffHz",
                FilterParameterValue.FromNumber(revised ? 8100 : 3900)),
            new("TapCount", FilterParameterValue.FromNumber(63)),
            new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
            new("PhaseResponse", FilterParameterValue.FromChoice("Linear")),
        ]);
        return filter;
    }

    private static FilterInstance TwoStageModulatedFilter(bool revised)
    {
        FilterDefinition definition = new((ObjectId)9472U,
            "C3 frozen modulated child", [
                new FilterStageDefinition(1, "BoundPole", FilterKind.OnePole),
                new FilterStageDefinition(2, "UnboundNotch", FilterKind.SvfNotch),
            ]);
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1500));
        filter.SetStageParameter(2, "CutoffHz",
            FilterParameterValue.FromNumber(revised ? 6900 : 820));
        filter.SetStageParameter(2, "Q",
            FilterParameterValue.FromNumber(revised ? 2.2 : 0.75));
        return filter;
    }

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }

    private static FilterInstance OnePole()
    {
        FilterDefinition definition = new((ObjectId)9471U, "Multi Sample pole");
        FilterInstance instance = new(definition);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        return instance;
    }

    private static SampleSound Sample(int frames)
    {
        float[] pcm = Enumerable.Range(0, frames)
            .Select(i => (float)(0.31 + 0.15 * Math.Sin(i * 0.097)
                + 0.07 * Math.Cos(i * 0.043))).ToArray();
        return new(new SampleDefinition(SampleId,
            "Slice 60 Filter Sample", new ExternalAssetReference("slice60.wav")),
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
