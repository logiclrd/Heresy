using System.Linq;
using System.Text.Json.Nodes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmNoisePersistenceTests
{
    [Test]
    public void NativeNoiseRoundTripsWithoutFrequencyOrSeedInSongSchema()
    {
        SongDocument song = new();
        ObjectId id = song.AllocateObjectId();
        FmSynthDefinition synth = new(id, "Hiss",
            new FmSynthGraph([
                new FmNoiseNode(4),
                new FmConstantNode(7, 0.25),
                new FmOperatorNode(9, FmOperatorKind.Multiply, [4, 7]),
            ], 9));
        song.Add(synth);
        string json = SongDocumentJson.Serialize(song,
            "/tmp/heresy-native-white-noise.hm.json");
        JsonObject noise = JsonNode.Parse(json)!["objects"]![id.Value.ToString()]!
            ["graph"]!["nodes"]!.AsArray().Single(node =>
                node!["type"]!.GetValue<string>() == "noise")!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(noise["id"]!.GetValue<int>(), Is.EqualTo(4));
            Assert.That(noise.ContainsKey("frequencyHz"), Is.False);
            Assert.That(noise.ContainsKey("seed"), Is.False,
                "seed belongs to each playback invocation, not the song definition");
        });
        SongDocument roundtrip = SongDocumentJson.Deserialize(json,
            "/tmp/heresy-native-white-noise.hm.json");
        FmSynthGraph graph =
            ((FmSynthDefinition)roundtrip.Objects[id]).Graph;
        Assert.That(graph.Nodes.Single(node => node.Id == 4),
            Is.TypeOf<FmNoiseNode>());
        Assert.That(graph.Nodes.Single(node => node.Id == 4).InputNodeIds,
            Is.Empty);
        Assert.That(graph.OutputNodeId, Is.EqualTo(9));
        SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(song);
        synth.Graph = new FmSynthGraph([new FmConstantNode(0, 0)], 0);
        Assert.That(((FmSynthDefinition)frozen.Document.Objects[id])
            .Graph.Nodes.OfType<FmNoiseNode>().Count(), Is.EqualTo(1));
    }
}
