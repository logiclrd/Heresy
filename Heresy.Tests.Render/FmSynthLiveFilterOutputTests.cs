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
public sealed class FmSynthLiveFilterOutputTests
{
    private static readonly ObjectId SampleId = (ObjectId)9360U;

    [Test]
    public void AudibleConstantGainAroundAContinuingOnePolePreservesItsHistory()
    {
        FilterInstance filter = OnePole();
        FmSynthSound original = OscillatorFiltered(filter, 0.95);
        FmSynthSound revised = OscillatorFiltered(filter, 0.18);
        AssertContinuousOutputEdit(original, revised);
    }

    [Test]
    public void OperatorModeChangeAfterAContinuingFilterGetsBoundedOutputFade()
    {
        FilterInstance filter = OnePole();
        FmSynthSound original = OscillatorFiltered(filter, 0.23, FmOperatorKind.Multiply);
        FmSynthSound revised = OscillatorFiltered(filter, 0.23, FmOperatorKind.Add);
        AssertContinuousOutputEdit(original, revised);
    }

    [Test]
    public void FilteredSampleGainEditPreservesOriginalPlayheadAndFilterTail()
    {
        FilterInstance filter = OnePole();
        SampleSound sample = Sample(400);
        FmSynthSound original = SampleFiltered(filter, sample, 0.9);
        FmSynthSound revised = SampleFiltered(filter, sample, 0.15);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState fresh = revised.CreateState();
        float[] previous = new float[128], after = new float[440],
            expected = new float[568];
        original.Render(context, held, 0, previous.Length, previous);
        revised.Render(context, fresh, 0, expected.Length, expected);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, after.Length, after);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        AssertFade(previous[^1], expected, after, 128,
            revised.GetEndFrameExclusive(context, held),
            excitationEnd: 400);
        revised.GetEndFrameExclusive(context, held).Should().Be(
            revised.GetEndFrameExclusive(context, fresh),
            "the Sample EOF and the downstream Filter tail cannot move"
            + " because an audible Constant was edited");
    }

    [TestCase(48000)]
    [TestCase(96000)]
    public void UnboundOnePoleCutoffGlidesLogarithmicallyOnCommonLiveClock(int sampleRate)
    {
        FmSynthSound Sound(double cutoff)
        {
            FilterInstance filter = OnePole();
            filter.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(cutoff));
            return new(new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 6000),
                new FmFilterNode(2, 1, filter),
            ], 2), new EmptyEnvelopes(), enableLiveMultiExpansion: true);
        }
        RenderContext context = new(new RenderConfiguration(sampleRate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        FmSynthSound previous = Sound(300);
        FmSynthSound revised = Sound(12000);
        SoundState held = previous.CreateState();
        int ramp = sampleRate / 10;
        float[] prior = new float[128 + ramp + 128],
            target = new float[prior.Length],
            actual = new float[ramp + 128];
        previous.Render(context, previous.CreateState(), 0, prior.Length, prior);
        revised.Render(context, revised.CreateState(), 0, target.Length, target);
        previous.Render(context, held, 0, 128, new float[128]);
        previous.TryPrepareRunningState(revised, held).Should().BeTrue();
        previous.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);

        // A sharp 300→12000 Hz coefficient retune produces an immediate
        // high-frequency burst. Logarithmic cutoff migration initially
        // stays close to the continuing lowpass and converges at 100 ms.
        for (int i = 0; i < 24; i++)
            Math.Abs(actual[i] - prior[128 + i]).Should().BeLessThan(0.04f,
                "cutoff must not jump immediately to 12 kHz at publication");
        for (int i = ramp + 32; i < actual.Length; i++)
            Math.Abs(actual[i] - target[128 + i]).Should().BeLessThan(0.01f,
                "the live OnePole must reach the authored cutoff while"
                + " retaining integrator continuity and phase");
    }

    [Test]
    public void FilterParameterOnlyConstantEditDoesNotHoldTheAudibleOutput()
    {
        FilterInstance filter = OnePole();
        FmSynthSound Build(double cutoff) => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 6100),
            new FmConstantNode(2, cutoff),
            new FmFilterNode(3, 1, filter, [
                new FmFilterParameterConnection(1, FilterKind.OnePole,
                    filter.GetStageGenerationForPersistence(1),
                    "CutoffHz", 2),
            ]),
        ], 3), new EmptyEnvelopes(), enableLiveMultiExpansion: true);

        FmSynthSound original = Build(8000);
        FmSynthSound revised = Build(600);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] before = new float[128], after = new float[3];
        original.Render(context, held, 0, before.Length, before);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, after.Length, after);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        Math.Abs(after[0] - before[^1]).Should().BeGreaterThan(1e-4f,
            "a Filter cutoff modulation port is a control input,"
            + " not a direct audio path deserving a 64-frame hold");
        after.Should().OnlyContain(sample => float.IsFinite(sample));
    }

    [Test]
    public void NonAuditionFilteredGainEditRemainsImmediate()
    {
        FilterInstance filter = OnePole();
        FmSynthSound original = OscillatorFiltered(filter, 0.95, audition: false);
        FmSynthSound revised = OscillatorFiltered(filter, 0.18, audition: false);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState fresh = revised.CreateState();
        original.Render(context, held, 0, 128, new float[128]);
        float[] expected = new float[129], actual = new float[1];
        revised.Render(context, fresh, 0, expected.Length, expected);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        revised.Render(context, held, 128, 1, actual);
        actual[0].Should().BeApproximately(expected[128], 3e-6f,
            "ordinary rendering must not add live-audition smoothing");
    }

    private static void AssertContinuousOutputEdit(
        FmSynthSound original, FmSynthSound revised)
    {
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        float[] previous = new float[128], actual = new float[160],
            expected = new float[288];
        original.Render(context, held, 0, previous.Length, previous);
        revised.Render(context, revised.CreateState(), 0,
            expected.Length, expected);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "continuing Filter history cannot require a second PCM state");
        AssertFade(previous[^1], expected, actual, 128);
    }

    private static void AssertFade(float previous, float[] expected,
        float[] actual, int start, long? endingFrame = null,
        long? excitationEnd = null)
    {
        actual[0].Should().Be(previous,
            "a changed audible property after a Filter must not click");
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Min(1.0, i / 4800.0);
            double w = t * t * (3 - 2 * t);
            float target = endingFrame.HasValue
                && start + i >= endingFrame.Value ? 0.0f
                : excitationEnd.HasValue && start + i >= excitationEnd.Value
                    ? expected[start + i] // Filter tail continues, no held sample
                    : (float)(previous * (1 - w) + expected[start + i] * w);
            actual[i].Should().BeApproximately(target,
                3e-6f,
                "the original Filter/Sample history must keep running while"
                + " the complete output transitions to revised controls");
        }
    }

    private static FmSynthSound OscillatorFiltered(FilterInstance filter,
        double gain, FmOperatorKind operation = FmOperatorKind.Multiply,
        bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 440),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.002),
            new FmConstantNode(3, gain),
            new FmOperatorNode(4, operation, [2, 3]),
        ], 4), new EmptyEnvelopes(), enableLiveMultiExpansion: audition);

    private static FmSynthSound SampleFiltered(FilterInstance filter,
        SampleSound sample, double gain)
    {
        SourceResolver resolver = new(sample);
        return new(new FmSynthGraph([
            new FmSoundSourceNode(1, SampleId, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.002),
            new FmConstantNode(3, gain),
            new FmOperatorNode(4, FmOperatorKind.Multiply, [2, 3]),
        ], 4), resolver, soundResolver: resolver,
            enableLiveMultiExpansion: true);
    }

    private static FilterInstance OnePole()
    {
        FilterDefinition definition = new((ObjectId)9361U, "Continuing pole");
        FilterInstance instance = new(definition);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        return instance;
    }

    private static SampleSound Sample(int frames)
    {
        float[] pcm = Enumerable.Range(0, frames)
            .Select(i => (float)(0.2 + 0.13 * Math.Sin(i * 0.07)
                + 0.05 * Math.Cos(i * 0.11))).ToArray();
        return new(new SampleDefinition(SampleId, "Filter tail sample",
            new ExternalAssetReference("slice55-filter.wav")),
            new MemorySampleData(48000, 1, pcm));
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }

    private sealed class SourceResolver(SampleSound sample)
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
