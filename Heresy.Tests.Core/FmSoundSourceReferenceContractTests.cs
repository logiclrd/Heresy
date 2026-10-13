using System;
using System.Linq;
using System.Text.Json.Nodes;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmSoundSourceReferenceContractTests
{
    [Test]
    public void SourceNodeHasStableValidatedModulationAndImmutableInputs()
    {
        ObjectId source = (ObjectId)42U;
        FmSoundSourceNode node = new(5, source, 440, 2,
            exponentialMultiplier: true, constant: true);
        Assert.Multiple(() =>
        {
            Assert.That(node.SourceId, Is.EqualTo(source));
            Assert.That(node.FrequencyHz, Is.EqualTo(440));
            Assert.That(node.MultiplierNodeId, Is.EqualTo(2));
            Assert.That(node.ExponentialMultiplier, Is.True);
            Assert.That(node.Constant, Is.True);
            Assert.That(node.InputNodeIds, Is.EqualTo(new[] { 2 }));
            Assert.That(new FmSoundSourceNode(6, ObjectId.None, 220)
                .InputNodeIds, Is.Empty);
        });
        Assert.That(() => new FmSoundSourceNode(1, source, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new FmSoundSourceNode(1, source, double.NaN),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new FmSoundSourceNode(1, source, 440, -1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new FmSynthGraph([
            node, new FmConstantNode(2, 1),
            new FmOperatorNode(7, FmOperatorKind.Add, [5, 2]),
        ], 7).Nodes.Count, Is.EqualTo(3));
        Assert.That(() => new FmSynthGraph([
            new FmSoundSourceNode(1, source, 440, 2),
            new FmOperatorNode(2, FmOperatorKind.Add, [1]),
        ], 1), Throws.ArgumentException.With.Message.Contains("cycle"));
    }

    [Test]
    public void SourceNodePersistsAndSnapshotsWithoutChangingObjectIds()
    {
        SongDocument song = new();
        ObjectId sourceId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(sourceId, "Child",
            new FmSynthGraph([new FmConstantNode(0, 0.25)], 0)));
        ObjectId ownerId = song.AllocateObjectId();
        FmSynthDefinition owner = new(ownerId, "Parent", new FmSynthGraph([
            new FmConstantNode(1, 2.0),
            new FmSoundSourceNode(2, sourceId, 329.6275569, 1, true, true),
        ], 2));
        song.Add(owner);
        string json = SongDocumentJson.Serialize(song,
            "/tmp/heresy-source-node-contract.hm.json");
        JsonObject node = JsonNode.Parse(json)!["objects"]![
            ownerId.Value.ToString()]!["graph"]!["nodes"]!.AsArray()
            .Single(x => x!["id"]!.GetValue<int>() == 2)!.AsObject();
        Assert.That(node["type"]!.GetValue<string>(), Is.EqualTo("soundSource"));
        Assert.That(node["sourceId"]!.GetValue<uint>(), Is.EqualTo(sourceId.Value));
        Assert.That(node["constant"]!.GetValue<bool>(), Is.True);

        SongDocument copy = SongDocumentJson.Deserialize(json,
            "/tmp/heresy-source-node-contract.hm.json");
        FmSoundSourceNode parsed = ((FmSynthDefinition)copy.Objects[ownerId])
            .Graph.Nodes.OfType<FmSoundSourceNode>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(parsed.SourceId, Is.EqualTo(sourceId));
            Assert.That(parsed.FrequencyHz, Is.EqualTo(329.6275569));
            Assert.That(parsed.MultiplierNodeId, Is.EqualTo(1));
            Assert.That(parsed.Constant, Is.True);
            Assert.That(parsed.ExponentialMultiplier, Is.True);
        });
        FmSoundSourceNode snap = ((FmSynthDefinition)
            SongDocumentSnapshot.Create(song).Document.Objects[ownerId])
            .Graph.Nodes.OfType<FmSoundSourceNode>().Single();
        Assert.That(snap.SourceId, Is.EqualTo(sourceId));
        Assert.That(snap, Is.Not.SameAs(owner.Graph.Nodes[1]));
    }

    [Test]
    public void SourceReferencesSurviveTombstonePruningAndDoNotInventNewLinks()
    {
        SongDocument song = new();
        ObjectId missing = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(missing, "Deleted",
            new FmSynthGraph([new FmConstantNode(0, 0)], 0)));
        song.Remove(missing);
        ObjectId synthId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(synthId, "Parent",
            new FmSynthGraph([new FmSoundSourceNode(0, missing, 220)], 0)));
        SongReferenceAnalysis analysis = SongReferenceAnalyzer.Analyze(song);
        Assert.That(analysis.References.Any(x =>
            x.Kind == SongReferenceKind.FmSynthSoundSource
            && x.TargetId == missing && x.SourceObjectId == synthId), Is.True);
        SongDocument restored = SongDocumentJson.Deserialize(
            SongDocumentJson.Serialize(song, "/tmp/heresy-source-ref.hm.json"),
            "/tmp/heresy-source-ref.hm.json");
        Assert.That(restored.Tombstones.ContainsKey(missing), Is.True);
    }

    [Test]
    public void CrossObjectSourceAndInstrumentCyclesAreRejectedWithoutMutation()
    {
        SongDocument song = new();
        ObjectId a = song.AllocateObjectId();
        ObjectId b = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(a, "A",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0)));
        song.Add(new FmSynthDefinition(b, "B",
            new FmSynthGraph([new FmSoundSourceNode(0, a, 440)], 0)));
        FmSynthGraph candidate = new(
            [new FmSoundSourceNode(0, b, 440)], 0);
        Assert.That(() => FmSoundSourceReferences.Validate(song, a, candidate),
            Throws.InvalidOperationException.With.Message.Contains("cycle"));
        Assert.That(((FmSynthDefinition)song.Objects[a]).Graph.Nodes.Single(),
            Is.TypeOf<FmConstantNode>());

        ObjectId instrumentId = song.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "Recursive");
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = a });
        song.Add(instrument);
        FmSynthGraph viaInstrument = new(
            [new FmSoundSourceNode(0, instrumentId, 440)], 0);
        Assert.That(() => FmSoundSourceReferences.Validate(song, a, viaInstrument),
            Throws.InvalidOperationException.With.Message.Contains("cycle"));
        Assert.DoesNotThrow(() =>
            FmSoundSourceReferences.Validate(song, a,
                new FmSynthGraph([new FmSoundSourceNode(0, ObjectId.None, 440)], 0)));
        Assert.DoesNotThrow(() =>
            FmSoundSourceReferences.Validate(song, a,
                new FmSynthGraph([new FmSoundSourceNode(0, (ObjectId)9999U, 440)], 0)));
    }
}
