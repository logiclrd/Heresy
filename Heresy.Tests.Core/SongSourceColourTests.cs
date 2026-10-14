using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongSourceColourTests
{
    [Test]
    public void HsvToArgbIsTwentyPercentAlphaWithSeventyFivePercentSaturation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SongSourceColour.FromHue(0), Is.EqualTo(0x33FF4040U));
            Assert.That(SongSourceColour.FromHue(120), Is.EqualTo(0x3340FF40U));
            Assert.That(SongSourceColour.FromHue(240), Is.EqualTo(0x334040FFU));
            Assert.That(() => SongSourceColour.FromHue(360),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
        HashSet<uint> generated = [];
        for (int i = 0; i < 512; i++)
        {
            uint colour = SongSourceColour.CreateRandom();
            byte red = (byte)(colour >> 16);
            byte green = (byte)(colour >> 8);
            byte blue = (byte)colour;
            Assert.That(colour >> 24, Is.EqualTo(0x33U));
            Assert.That(Math.Max(red, Math.Max(green, blue)), Is.EqualTo(255));
            Assert.That(Math.Min(red, Math.Min(green, blue)), Is.EqualTo(64));
            generated.Add(colour);
        }
        Assert.That(generated.Count, Is.GreaterThan(360),
            "8-bit RGB quantizes the full 360-degree hue wheel; independent draws should still cover many distinct tints");
    }

    [Test]
    public void AllPlayableKindsHaveColoursAndNonSourcesNeverDo()
    {
        SongObject[] sources = AllKinds();
        Assert.That(sources, Has.Length.EqualTo(8));
        foreach (SongObject source in sources)
        {
            Assert.That(source.SourceColourArgb, Is.Not.Null,
                source.GetType().Name);
            Assert.That(source.SourceColourArgb!.Value >> 24, Is.EqualTo(0x33U));
        }
        FilterDefinition filter = new((ObjectId)201U, "Filter");
        NodeEnvelopeDefinition envelope = new((ObjectId)202U, "Envelope");
        Assert.Multiple(() =>
        {
            Assert.That(filter.SourceColourArgb, Is.Null);
            Assert.That(envelope.SourceColourArgb, Is.Null);
            Assert.That(() => filter.CopySourceColourFrom(sources[0]),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void JsonAndSnapshotPreserveEachColourAndDoNotChangeRevisions()
    {
        SongDocument song = new();
        SongObject[] sources = AllKinds().Where(s => s is not SampleDefinition).ToArray();
        foreach (SongObject source in sources)
            song.Add(source);
        song.Add(new FilterDefinition((ObjectId)201U, "Filter"));
        uint documentRevision = song.DocumentRevision;
        uint audioRevision = song.AudioRevision;
        string path = Path.Combine(Path.GetTempPath(), "source-colour-roundtrip.hm.json");
        string json = SongDocumentJson.Serialize(song, path);
        JsonObject persisted = JsonNode.Parse(json)!["objects"]!.AsObject();
        foreach (SongObject source in sources)
            Assert.That(persisted[source.Id.Value.ToString()]!["sourceColourArgb"]!
                .GetValue<uint>(), Is.EqualTo(source.SourceColourArgb));
        Assert.That(persisted["201"]!.AsObject().ContainsKey("sourceColourArgb"),
            Is.False);
        SongDocument loaded = SongDocumentJson.Deserialize(json, path);
        foreach (SongObject source in sources)
            Assert.That(loaded.Objects[source.Id].SourceColourArgb,
                Is.EqualTo(source.SourceColourArgb));
        SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(song);
        foreach (SongObject source in sources)
            Assert.That(frozen.Document.Objects[source.Id].SourceColourArgb,
                Is.EqualTo(source.SourceColourArgb));
        Assert.Multiple(() =>
        {
            Assert.That(song.DocumentRevision, Is.EqualTo(documentRevision));
            Assert.That(song.AudioRevision, Is.EqualTo(audioRevision));
            Assert.That(loaded.DocumentRevision, Is.Zero);
            Assert.That(loaded.AudioRevision, Is.Zero);
        });
    }

    [Test]
    public void LegacyMissingColoursAreAssignedOnceAndPersistOnNextSave()
    {
        SongDocument original = new();
        foreach (SongObject source in AllKinds().Where(s => s is not SampleDefinition))
            original.Add(source);
        string path = Path.Combine(Path.GetTempPath(), "legacy-source-colours.hm.json");
        JsonObject manifest = JsonNode.Parse(
            SongDocumentJson.Serialize(original, path))!.AsObject();
        JsonObject objects = manifest["objects"]!.AsObject();
        foreach (var item in objects)
            item.Value!.AsObject().Remove("sourceColourArgb");
        SongDocument legacy = SongDocumentJson.Deserialize(manifest.ToJsonString(), path);
        Assert.That(legacy.DocumentRevision, Is.Zero);
        Assert.That(legacy.AudioRevision, Is.Zero);
        Dictionary<ObjectId, uint?> assigned = legacy.Objects.ToDictionary(
            entry => entry.Key, entry => entry.Value.SourceColourArgb);
        Assert.That(assigned.Values, Is.All.Not.Null);
        string resaved = SongDocumentJson.Serialize(legacy, path);
        SongDocument reopened = SongDocumentJson.Deserialize(resaved, path);
        SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(legacy);
        foreach (ObjectId id in assigned.Keys)
        {
            Assert.That(reopened.Objects[id].SourceColourArgb, Is.EqualTo(assigned[id]));
            Assert.That(snapshot.Document.Objects[id].SourceColourArgb,
                Is.EqualTo(assigned[id]));
        }
        Assert.That(legacy.DocumentRevision, Is.Zero,
            "legacy metadata migration is not an authored song modification");
    }

    [Test]
    public void InvalidPersistedAlphaAndColourOnNonSourceRejectSafely()
    {
        SongDocument song = new();
        song.Add(new DataPatternDefinition((ObjectId)1U, "Pattern"));
        song.Add(new NodeEnvelopeDefinition((ObjectId)2U, "Envelope"));
        string path = Path.Combine(Path.GetTempPath(), "invalid-source-colour.hm.json");
        JsonObject json = JsonNode.Parse(SongDocumentJson.Serialize(song, path))!.AsObject();
        json["objects"]!["1"]!["sourceColourArgb"] = 0xFFFF4040U;
        Assert.That(() => SongDocumentJson.Deserialize(json.ToJsonString(), path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("source colour"));
        json["objects"]!["1"]!["sourceColourArgb"] = 0x33FF4040U;
        json["objects"]!["2"]!["sourceColourArgb"] = 0x33FF4040U;
        Assert.That(() => SongDocumentJson.Deserialize(json.ToJsonString(), path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("source colour"));
    }

    [Test]
    public void PackedHmRoundTripPreservesSampleAndOtherSourceColours()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"heresy-source-colours-{Guid.NewGuid():N}.hm");
        SongDocument song = new();
        SampleDefinition sample = (SampleDefinition)AllKinds()[0];
        song.Add(sample);
        song.Add(new DataPatternDefinition((ObjectId)3U, "Sequence pattern"));
        uint sourceColour = sample.SourceColourArgb!.Value;
        try
        {
            SongDocumentPackage.Save(path, song);
            SongDocument loaded = SongDocumentPackage.Load(path);
            Assert.That(loaded.Objects[sample.Id].SourceColourArgb,
                Is.EqualTo(sourceColour));
            Assert.That(loaded.Objects[(ObjectId)3U].SourceColourArgb,
                Is.EqualTo(song.Objects[(ObjectId)3U].SourceColourArgb));
            SongDocumentPackage.Save(path, loaded);
            SongDocument reopened = SongDocumentPackage.Load(path);
            Assert.That(reopened.Objects[sample.Id].SourceColourArgb,
                Is.EqualTo(sourceColour));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static SongObject[] AllKinds()
    {
        SampleDefinition sample = SampleDefinition.CreateImported(
            (ObjectId)1U, "Sample", "test.wav", Wave());
        return [
            sample,
            new FmSynthDefinition((ObjectId)2U, "Synth",
                new FmSynthGraph([new FmConstantNode(0, 1)], 0)),
            new DataPatternDefinition((ObjectId)3U, "Pattern"),
            new ScriptPatternDefinition((ObjectId)4U, "Script Pattern"),
            new InstrumentDefinition((ObjectId)5U, "Instrument"),
            new ScriptInstrumentDefinition((ObjectId)6U, "Script Instrument"),
            new DataSequenceDefinition((ObjectId)7U, "Sequence"),
            new ScriptSequenceDefinition((ObjectId)8U, "Script Sequence"),
        ];
    }

    private static byte[] Wave()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(38);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(2);
            writer.Write((short)8192);
        }
        return stream.ToArray();
    }
}
