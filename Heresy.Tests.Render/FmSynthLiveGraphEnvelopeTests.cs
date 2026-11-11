using System;
using System.Numerics;

using AwesomeAssertions;
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
            double t = Math.Clamp(i / 64.0, 0.0, 1.0);
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
        before.Render(context, held, 0, 128, new float[128]);
        after.Render(context, reference, 0, 128, new float[128]);
        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        // Complete the bounded transition before Note Off. The revised
        // envelope must then release at the original running note age.
        after.Render(context, held, 128, 128, new float[128]);
        after.Render(context, reference, 128, 128, new float[128]);
        TimeSpan noteOff = FrameTime.FrameStartTime(256, 48000);
        held.NoteOffTime = reference.NoteOffTime = noteOff;
        float[] actual = new float[192], expected = new float[192];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 256, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0L);
        after.Render(context, reference, 256, expected.Length, expected);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[i], 2e-6f,
                "a held curve edit must not rebase its future release clock");
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
