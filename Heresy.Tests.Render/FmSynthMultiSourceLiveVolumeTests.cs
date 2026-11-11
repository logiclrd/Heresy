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
using Heresy.Render.Spatial;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthMultiSourceLiveVolumeTests
{
    [Test]
    public void SharedVolumeEditAdoptsAtomicallyAcrossTwoInstrumentSourcesUnderAnOperator()
    {
        // Both source invocations are already sounding when a shared node
        // Volume curve is replaced. A graph-level edit must affect BOTH,
        // not merely the first branch or the definition of future notes.
        ObjectId sampleId = (ObjectId)8100U;
        ObjectId instrumentId = (ObjectId)8101U;
        ObjectId envelopeId = (ObjectId)8102U;
        float[] pcm = Enumerable.Range(0, 4096)
            .Select(i => (float)(0.39 + 0.15 * Math.Sin(i * 0.17)))
            .ToArray();
        SampleSound sample = new(
            new SampleDefinition(sampleId, "Two source PCM",
                new ExternalAssetReference("two-source-pcm.wav")),
            new MemorySampleData(48000, 1, pcm));
        OneSourceResolver samples = new(sampleId, sample);

        NodeEnvelopeCurve Curve(double level)
        {
            NodeEnvelopeDefinition definition = new(envelopeId, "Shared Volume");
            definition.ReplaceCurve([
                new(TimeSpan.Zero, level),
                new(TimeSpan.FromTicks(208), level),
                new(TimeSpan.FromTicks(2291), 0.0),
            ], 1, 1, EnvelopeSustainLoopMode.Forward);
            return new NodeEnvelopeCurve(definition);
        }
        Heresy.Core.Instruments.InstrumentDefinition Instrument()
        {
            Heresy.Core.Instruments.InstrumentDefinition definition =
                new(instrumentId, "Two source Instrument");
            definition.ToneSpecifications.Add(
                new Heresy.Core.Instruments.ToneSpecification
                {
                    SourceId = sampleId, VolumeEnvelopeId = envelopeId,
                });
            definition.ToneTable.Add(0);
            return definition;
        }

        OneEnvelopeResolver oldCurves = new(envelopeId, Curve(1.0));
        OneEnvelopeResolver newCurves = new(envelopeId, Curve(0.25));
        Heresy.Render.Instruments.InstrumentSound oldInstrument =
            new(Instrument(), samples, oldCurves);
        Heresy.Render.Instruments.InstrumentSound newInstrument =
            new(Instrument(), samples, newCurves);
        FmSynthGraph graph = new([
            new FmSoundSourceNode(1, instrumentId,
                FmMultiNode.NeutralFrequencyHz),
            new FmSoundSourceNode(2, instrumentId,
                FmMultiNode.NeutralFrequencyHz),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
        ], 3);
        FmSynthSound before = new(graph, oldCurves,
            soundResolver: new OneSourceResolver(instrumentId, oldInstrument),
            enableLiveMultiExpansion: true);
        FmSynthSound after = new(graph, newCurves,
            soundResolver: new OneSourceResolver(instrumentId, newInstrument),
            enableLiveMultiExpansion: true);
        RenderContext context = new(new RenderConfiguration(48000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        SoundState held = before.CreateState(), oldReference = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        before.Render(context, oldReference, 0, 128, new float[128]);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        float[] actual = new float[160], reference = new float[160];
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocations).Should().Be(0L);
        before.Render(context, oldReference, 128, reference.Length, reference);
        for (int i = 0; i < actual.Length; i++)
        {
            double t = Math.Clamp(i / 64.0, 0.0, 1.0);
            double w = t * t * (3.0 - 2.0 * t);
            actual[i].Should().BeApproximately(
                (float)(reference[i] * (1.0 - 0.75 * w)), 2e-5f,
                "both already-sounding Instrument Sources must adopt the new curve");
        }
        actual[0].Should().Be(reference[0]);
        Math.Abs(actual[100]).Should().BeLessThan(
            Math.Abs(reference[100]) * 0.26f);

        held.NoteOffTime = TimeSpan.FromSeconds(320.0 / 48000);
        after.GetEndFrameExclusive(context, held).Should().Be(330,
            "the existing shared release clock remains intact under the Operator");
        float[] released = new float[56];
        after.Render(context, held, 288, released.Length, released);
        released[32].Should().NotBe(0);
        released.Skip(42).Should().OnlyContain(v => v == 0.0f);
    }

    [Test]
    public void IncompatibleSecondSampleRejectsEveryVolumeEditButNotIndependentScalarChanges()
    {
        // Two different Instrument Sources share the same output graph.
        // The first selected PCM is unchanged; the second one has been
        // replaced behind its unchanged authored SourceId. Volume adoption
        // must be all-or-none, without blocking a compatible Constant edit.
        ObjectId sampleA = (ObjectId)8110U, sampleB = (ObjectId)8111U;
        ObjectId instrumentA = (ObjectId)8112U, instrumentB = (ObjectId)8113U;
        ObjectId envelopeA = (ObjectId)8114U, envelopeB = (ObjectId)8115U;

        SampleSound Sample(ObjectId id, float value) => new(
            new SampleDefinition(id, "Atomic PCM",
                new ExternalAssetReference("atomic-pcm.wav")),
            new MemorySampleData(48000, 1,
                Enumerable.Repeat(value, 4096).ToArray()));
        SampleSound unchanged = Sample(sampleA, 0.2f);
        SampleSound previousB = Sample(sampleB, 0.12f);
        SampleSound incompatibleB = Sample(sampleB, 0.07f);

        NodeEnvelopeCurve Curve(ObjectId id, double level)
        {
            NodeEnvelopeDefinition definition = new(id, "Atomic Volume");
            definition.ReplaceCurve([
                new(TimeSpan.Zero, level),
                new(TimeSpan.FromTicks(208), level),
                new(TimeSpan.FromTicks(2291), 0.0),
            ], 1, 1, EnvelopeSustainLoopMode.Forward);
            return new NodeEnvelopeCurve(definition);
        }

        Heresy.Core.Instruments.InstrumentDefinition Instrument(
            ObjectId id, ObjectId sampleId, ObjectId envelopeId)
        {
            Heresy.Core.Instruments.InstrumentDefinition definition =
                new(id, "Atomic Instrument");
            definition.ToneSpecifications.Add(
                new Heresy.Core.Instruments.ToneSpecification
                {
                    SourceId = sampleId, VolumeEnvelopeId = envelopeId,
                });
            definition.ToneTable.Add(0);
            return definition;
        }

        TwoEnvelopeResolver oldCurves = new(envelopeA, Curve(envelopeA, 1.0),
            envelopeB, Curve(envelopeB, 1.0));
        TwoEnvelopeResolver newCurves = new(envelopeA, Curve(envelopeA, 0.25),
            envelopeB, Curve(envelopeB, 0.5));
        TwoSourceResolver oldSamples = new(sampleA, unchanged, sampleB, previousB);
        TwoSourceResolver newSamples = new(sampleA, unchanged, sampleB, incompatibleB);
        TwoSourceResolver oldInstruments = new(
            instrumentA,
            new Heresy.Render.Instruments.InstrumentSound(
                Instrument(instrumentA, sampleA, envelopeA), oldSamples, oldCurves),
            instrumentB,
            new Heresy.Render.Instruments.InstrumentSound(
                Instrument(instrumentB, sampleB, envelopeB), oldSamples, oldCurves));
        TwoSourceResolver newInstruments = new(
            instrumentA,
            new Heresy.Render.Instruments.InstrumentSound(
                Instrument(instrumentA, sampleA, envelopeA), newSamples, newCurves),
            instrumentB,
            new Heresy.Render.Instruments.InstrumentSound(
                Instrument(instrumentB, sampleB, envelopeB), newSamples, newCurves));

        FmSynthGraph Graph(double scalar) => new([
            new FmSoundSourceNode(1, instrumentA,
                FmMultiNode.NeutralFrequencyHz),
            new FmSoundSourceNode(2, instrumentB,
                FmMultiNode.NeutralFrequencyHz),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            new FmConstantNode(4, scalar),
            new FmOperatorNode(5, FmOperatorKind.Multiply, [3, 4]),
        ], 5);
        FmSynthSound before = new(Graph(1.0), oldCurves,
            soundResolver: oldInstruments, enableLiveMultiExpansion: true);
        FmSynthSound after = new(Graph(0.5), newCurves,
            soundResolver: newInstruments, enableLiveMultiExpansion: true);
        RenderContext context = new(new RenderConfiguration(48000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        SoundState held = before.CreateState(), reference = before.CreateState();
        before.Render(context, held, 0, 128, new float[128]);
        before.Render(context, reference, 0, 128, new float[128]);

        before.TryPrepareRunningState(after, held).Should().BeTrue();
        before.CommitPreparedRunningState(after, held);
        float[] actual = new float[160], original = new float[160];
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        after.Render(context, held, 128, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocations).Should().Be(0L);
        before.Render(context, reference, 128, original.Length, original);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(original[i] * 0.5f, 2e-5f,
                "an incompatible selected Sample must reject both Volume changes while the independent Constant edit still adopts");
    }

    private sealed class TwoSourceResolver(
        ObjectId firstId, ISound first, ObjectId secondId, ISound second)
        : ISoundResolver
    {
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            sound = sourceId == firstId ? first :
                sourceId == secondId ? second : null;
            return sound is not null;
        }
    }

    private sealed class TwoEnvelopeResolver(
        ObjectId firstId, IEnvelopeCurve first,
        ObjectId secondId, IEnvelopeCurve second)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = requested == firstId ? first :
                requested == secondId ? second : null;
            return curve is not null;
        }
    }

    private sealed class OneSourceResolver(ObjectId id, ISound resolved) : ISoundResolver
    {
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            sound = sourceId == id ? resolved : null;
            return sound is not null;
        }
    }

    private sealed class OneEnvelopeResolver(ObjectId id, IEnvelopeCurve selected)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = requested == id ? selected : null;
            return curve is not null;
        }
    }
}
