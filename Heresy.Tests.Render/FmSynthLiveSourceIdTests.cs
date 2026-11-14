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
    public void DifferentSampleSourceIdStillRefusesUnpreparedRunningStateMigration()
    {
        SampleSound originalSample = Sample(FirstId, Enumerable.Range(0, 2400)
            .Select(i => (float)Math.Sin(i * 0.04)).ToArray());
        SampleSound differentSample = Sample(AliasId, Enumerable.Range(0, 2400)
            .Select(i => (float)Math.Cos(i * 0.09)).ToArray());
        SourceResolver resolver = new(FirstId, originalSample, AliasId,
            differentSample);
        FmSynthSound original = Sound(FirstId, resolver);
        FmSynthSound revised = Sound(AliasId, resolver);
        SoundState held = original.CreateState();
        SoundState comparison = original.CreateState();
        RenderContext context = Mono();
        original.Render(context, held, 0, 128, new float[128]);
        original.Render(context, comparison, 0, 128, new float[128]);

        original.CanAdoptRunningState(revised, held).Should().BeFalse(
            "an arbitrary new PCM asset needs a separate prepared onset and fade");
        original.TryPrepareRunningState(revised, held).Should().BeFalse();
        float[] continued = new float[150], expected = new float[150];
        original.Render(context, held, 128, continued.Length, continued);
        original.Render(context, comparison, 128, expected.Length, expected);
        continued.Should().Equal(expected,
            "declined migration must never partially mutate the held Sample");
        float[] nextNote = new float[20];
        revised.Render(context, revised.CreateState(), 0, nextNote.Length,
            nextNote);
        nextNote[0].Should().NotBeApproximately(expected[0], 0.2f,
            "future notes must still use the new selected Sample");
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
