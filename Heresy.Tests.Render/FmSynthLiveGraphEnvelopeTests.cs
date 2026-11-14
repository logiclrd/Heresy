using System;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.Samples;
using Heresy.Render.Samples;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthLiveGraphEnvelopeTests
{
    private static readonly ObjectId OriginalId = (ObjectId)9130U;
    private static readonly ObjectId OtherId = (ObjectId)9131U;

    [TestCase(false)]
    [TestCase(true)]
    public void AudibleNodeEnvelopeChangeCrossfadesAtOriginalClockAndPhase(
        bool changeEnvelopeSelection)
    {
        NodeEnvelopeCurve oldCurve = Sustained(1.0);
        NodeEnvelopeCurve newCurve = Sustained(0.2);
        FmSynthSound oldSound = GainSound(OriginalId, oldCurve);
        FmSynthSound newSound = GainSound(
            changeEnvelopeSelection ? OtherId : OriginalId, newCurve);
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        float[] before = new float[128], actual = new float[128],
            reference = new float[256];
        oldSound.Render(context, held, 0, before.Length, before);
        newSound.Render(context, newSound.CreateState(), 0, reference.Length, reference);

        oldSound.TryPrepareRunningState(newSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(newSound, held);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        newSound.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "node Envelope smoothing may not allocate in PCM");

        actual[0].Should().Be(before[^1],
            "live graph Envelope edits must not abruptly replace audible gain");
        for (int i = 0; i < 128; i++)
        {
            double t = Math.Clamp(i / 4800.0, 0.0, 1.0);
            double w = t * t * (3.0 - 2.0 * t);
            actual[i].Should().BeApproximately(
                (float)(before[^1] * (1.0 - w) + reference[128 + i] * w),
                2e-6f,
                "the edited shared curve runs at the original note clock"
                + " while the Oscillator keeps its original phase");
        }
    }

    [Test]
    public void HeldEnvelopeEditKeepsItsRevisedSustainAndLaterReleaseClock()
    {
        FmSynthSound before = GainSound(OriginalId, Sustained(1.0));
        FmSynthSound after = GainSound(OriginalId, Sustained(0.2));
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        SoundState reference = after.CreateState();
        SoundState oldReference = before.CreateState();
        float[] lastOld = new float[128];
        before.Render(context, held, 0, 128, lastOld);
        before.Render(context, oldReference, 0, 128, new float[128]);
        after.Render(context, reference, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        // Complete the bounded transition before Note Off. The revised
        // envelope must then release at the original running note age.
        after.Render(context, held, 128, 128, new float[128]);
        before.Render(context, oldReference, 128, 128, new float[128]);
        after.Render(context, reference, 128, 128, new float[128]);
        TimeSpan noteOff = FrameTime.FrameStartTime(256, 48000);
        held.NoteOffTime = reference.NoteOffTime = oldReference.NoteOffTime
            = noteOff;
        float[] actual = new float[192], expected = new float[192],
            oldValues = new float[192];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 256, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        after.Render(context, reference, 256, expected.Length, expected);
        before.Render(context, oldReference, 256, oldValues.Length, oldValues);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp((128.0 + i) / 4800.0, 0.0, 1.0);
            double w = t * t * (3.0 - 2.0 * t);
            long? end = after.GetEndFrameExclusive(context, held);
            float interpolated = end.HasValue && 256 + i >= end.Value
                ? 0.0f
                : (float)(lastOld[^1] * (1 - w) + expected[i] * w);
            actual[i].Should().BeApproximately(interpolated, 2e-6f,
                "the revised sustain/release must keep its original clock"
                + " even while the common 100 ms fade is in progress");
        }
        actual[^1].Should().Be(0.0f,
            "the revised sustain loop must eventually release to its natural end");
    }

    [Test]
    public void EquivalentFrozenNodeCurveDoesNotStartAnUnnecessaryFade()
    {
        FmSynthSound oldSound = GainSound(OriginalId, Rising());
        FmSynthSound newSound = GainSound(OriginalId, Rising());
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        float[] before = new float[128], after = new float[100],
            reference = new float[228];
        oldSound.Render(context, held, 0, before.Length, before);
        newSound.Render(context, newSound.CreateState(), 0, reference.Length, reference);
        oldSound.TryPrepareRunningState(newSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(newSound, held);
        newSound.Render(context, held, 128, after.Length, after);
        for (int i = 0; i < after.Length; i++)
            after[i].Should().BeApproximately(reference[128 + i], 2e-6f,
                "identical frozen node curves must not cause a spurious hold");
    }

    [Test]
    public void EnvelopeUsedOnlyAsFrequencyControlDoesNotFadeAudibleOutput()
    {
        FmSynthSound Build(double multiplier)
        {
            NodeEnvelopeCurve curve = Sustained(multiplier);
            return new(new FmSynthGraph([
                new FmEnvelopeNode(1, OriginalId),
                new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 100,
                    multiplierNodeId: 1),
            ], 2), new SingleCurve(OriginalId, curve),
                enableLiveMultiExpansion: true);
        }
        FmSynthSound oldSound = Build(1);
        FmSynthSound newSound = Build(3);
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        oldSound.Render(context, held, 0, 128, new float[128]);
        oldSound.TryPrepareRunningState(newSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(newSound, held);
        float[] output = new float[2];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        newSound.Render(context, held, 128, 2, output);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        output[0].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * (100 * 128.0) / 48000), 2e-6f);
        output[1].Should().BeApproximately(
            (float)Math.Sin(2 * Math.PI * (100 * 128.0 + 300) / 48000), 2e-6f,
            "the live Envelope controls subsequent pitch, not output gain");
    }

    [Test]
    public void AudibleReleaseAlreadyStartedRetainsItsOriginalFrozenCurve()
    {
        FmSynthSound oldSound = GainSound(OriginalId, Sustained(1));
        FmSynthSound newSound = GainSound(OriginalId, Sustained(0.25));
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        oldSound.Render(context, held, 0, 128, new float[128]);
        held.NoteOffTime = FrameTime.FrameStartTime(128, 48000);
        oldSound.CanAdoptRunningState(newSound, held).Should().BeFalse(
            "already-releasing node curves need a separate migration"
            + " policy that preserves their release tails and origin");
        oldSound.TryPrepareRunningState(newSound, held).Should().BeFalse();
    }

    [Test]
    public void OrdinaryPlaybackDoesNotApplyAuditionEnvelopeFade()
    {
        FmSynthSound oldSound = GainSound(OriginalId, Sustained(1), false);
        FmSynthSound newSound = GainSound(OriginalId, Sustained(0.2), false);
        RenderContext context = Mono();
        SoundState held = oldSound.CreateState();
        oldSound.Render(context, held, 0, 128, new float[128]);
        oldSound.TryPrepareRunningState(newSound, held).Should().BeTrue();
        oldSound.CommitPreparedRunningState(newSound, held);
        float[] output = new float[1];
        newSound.Render(context, held, 128, 1, output);
        output[0].Should().BeApproximately(
            (float)(0.2 * Math.Sin(2 * Math.PI * 440 * 128 / 48000)),
            2e-6f);
    }


    [Test]
    public void ScriptEnvelopeChangedInsideMultiFadesBeforePreservingParentFilterHistory()
    {
        const int edit = 128, fade = 4800, continuation = fade + 96;
        ObjectId scriptId = (ObjectId)9132U;
        ObjectId referenceId = (ObjectId)9133U;
        FilterInstance filter = new(new FilterDefinition(
            (ObjectId)9134U, "Script child pole"));
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1500));
        FmSynthSound Build(double scriptLevel) => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 360),
            new FmEnvelopeNode(2, scriptId),
            new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
            new FmMultiNode(4, 3, voiceCount: 3, detune: 0,
                randomizePhase: false, gainCompensation: true),
            new FmFilterNode(5, 4, filter, tailSeconds: 0.003),
        ], 5), new SingleCurve(scriptId,
            new ScriptEnvelopeCurve((_, _) => scriptLevel, null)),
            enableLiveMultiExpansion: true);

        FmSynthSound before = Build(0.8);
        FmSynthSound after = Build(0.12);
        RenderContext context = Mono();
        SoundState held = before.CreateState();
        before.Render(context, held, 0, edit, new float[edit]);
        before.CanAdoptRunningState(after, held).Should().BeTrue();
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);

        // A Script edit must smooth at each Multi CHILD output, while the
        // continuing parent OnePole integrates the MOVING new signal.
        // All three non-randomized voices are phase-aligned with unity sum.
        float[] baked = new float[edit + continuation];
        double previous = Math.Sin(2 * Math.PI * 360 * (edit - 1) / 48000) * 0.8;
        for (int frame = 0; frame < baked.Length; frame++)
        {
            double moving = Math.Sin(2 * Math.PI * 360 * frame / 48000);
            if (frame < edit)
                baked[frame] = (float)(moving * 0.8);
            else
            {
                double t = Math.Clamp((double)(frame - edit) / fade, 0, 1);
                double w = t * t * (3 - 2 * t);
                baked[frame] = (float)(previous * (1 - w) + moving * 0.12 * w);
            }
        }
        SampleSound sample = new(new SampleDefinition(
            referenceId, "Script envelope oracle",
            new ExternalAssetReference("script-envelope-oracle.wav")),
            new MemorySampleData(48000, 1, baked));
        FmSynthSound oracle = new(new FmSynthGraph([
            new FmSoundSourceNode(1, referenceId,
                FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 3, detune: 0,
                randomizePhase: false, gainCompensation: true),
            new FmFilterNode(3, 2, filter, tailSeconds: 0.003),
        ], 3), new SingleCurve(referenceId,
            new ScriptEnvelopeCurve((_, _) => 1, null)),
            soundResolver: new OneSource(referenceId, sample),
            enableLiveMultiExpansion: true);
        float[] expected = new float[baked.Length];
        oracle.Render(context, oracle.CreateState(), 0,
            expected.Length, expected);
        float[] actual = new float[continuation];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, edit, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L,
            "Script changes inside Multi may not allocate or restart child states in PCM");
        for (int i = 0; i < continuation; i++)
            actual[i].Should().BeApproximately(expected[edit + i], 7e-5f,
                $"every scripted child must fade before the continuous parent IIR at {edit + i}");
    }

    private sealed class OneSource(ObjectId id, SampleSound sample) : ISoundResolver
    {
        public bool TryResolve(ObjectId requested, bool mixdown,
            out ISound? sound)
        {
            sound = requested == id ? sample : null;
            return sound is not null;
        }
    }

    private static FmSynthSound GainSound(ObjectId envelopeId,
        NodeEnvelopeCurve curve, bool audition = true)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 440),
            new FmEnvelopeNode(2, envelopeId),
            new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
        ], 3), new SingleCurve(envelopeId, curve),
            enableLiveMultiExpansion: audition);

    private static NodeEnvelopeCurve Sustained(double level)
    {
        NodeEnvelopeDefinition definition = new(OriginalId, "Shared gain");
        definition.ReplaceCurve([
            new(TimeSpan.Zero, level),
            new(TimeSpan.FromMilliseconds(1), level),
            new(TimeSpan.FromMilliseconds(2), level),
            new(TimeSpan.FromMilliseconds(5), 0),
        ], 2, 2, EnvelopeSustainLoopMode.Forward);
        return new(definition);
    }

    private static NodeEnvelopeCurve Rising()
    {
        NodeEnvelopeDefinition definition = new(OriginalId, "Unchanged ramp");
        definition.ReplaceCurve([
            new(TimeSpan.Zero, 0.3),
            new(TimeSpan.FromMilliseconds(100), 1),
            new(TimeSpan.FromMilliseconds(101), 1),
            new(TimeSpan.FromMilliseconds(150), 0),
        ], 2, 2, EnvelopeSustainLoopMode.Forward);
        return new(definition);
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class SingleCurve(ObjectId id, NodeEnvelopeCurve envelope)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = requested == id ? envelope : null;
            return curve is not null;
        }
    }
}
