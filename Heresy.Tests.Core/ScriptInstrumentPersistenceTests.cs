using System;
using System.IO;
using System.Linq;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ScriptInstrumentPersistenceTests
{
    [Test]
    public void JsonAndHmPreserveSelectorToneReferencesAndSnapshotIsolation()
    {
        SongDocument song = new();
        ObjectId selected = song.AllocateObjectId();
        ObjectId envelope = song.AllocateObjectId();
        song.Add(new AdsrEnvelopeDefinition(envelope, "Volume"));
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition source = new(id, "Selectable")
        { Source = "pitchMultiplier < 1.2 ? 0 : -1",
          PitchExpression = "pitchMultiplier * 0.5" };
        source.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = selected, PitchMultiplier = 1.5,
            VolumeEnvelopeId = envelope,
        });
        song.Add(source);
        string jsonPath = Path.Combine(Path.GetTempPath(),
            "heresy-selector-" + Guid.NewGuid().ToString("N") + ".hm.json");
        SongDocument roundTrip = SongDocumentJson.Deserialize(
            SongDocumentJson.Serialize(song, jsonPath), jsonPath);
        ScriptInstrumentDefinition restored =
            (ScriptInstrumentDefinition)roundTrip.Objects[id];
        Assert.Multiple(() =>
        {
            Assert.That(restored.Source, Is.EqualTo(source.Source));
            Assert.That(restored.PitchExpression, Is.EqualTo(source.PitchExpression));
            Assert.That(restored.ToneSpecifications.Single().SourceId,
                Is.EqualTo(selected));
            Assert.That(restored.ToneSpecifications.Single().VolumeEnvelopeId,
                Is.EqualTo(envelope));
            Assert.That(restored.ToneSpecifications.Single().PitchMultiplier,
                Is.EqualTo(1.5));
        });
        // Older schema-v1 scripted Instruments without this optional field
        // retain the default identity pitch transform.
        System.Text.Json.Nodes.JsonObject manifest =
            System.Text.Json.Nodes.JsonNode.Parse(
                SongDocumentJson.Serialize(song, jsonPath))!.AsObject();
        manifest["objects"]![id.Value.ToString()]!.AsObject()
            .Remove("pitchExpression");
        ScriptInstrumentDefinition legacy =
            (ScriptInstrumentDefinition)SongDocumentJson.Deserialize(
                manifest.ToJsonString(), jsonPath).Objects[id];
        Assert.That(legacy.PitchExpression, Is.EqualTo("1.0"));
        SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(song);
        source.Source = "-1";
        Assert.That(((ScriptInstrumentDefinition)frozen.Document.Objects[id]).Source,
            Is.EqualTo(restored.Source));
        string hmPath = Path.ChangeExtension(jsonPath, ".hm");
        try
        {
            SongDocumentStorage.Save(hmPath, song);
            SongDocument loaded = SongDocumentStorage.Load(hmPath);
            Assert.That(((ScriptInstrumentDefinition)loaded.Objects[id])
                .ToneSpecifications.Single().VolumeEnvelopeId,
                Is.EqualTo(envelope));
        }
        finally
        {
            if (File.Exists(hmPath)) File.Delete(hmPath);
        }
    }

    [Test]
    public void DeclaredToneReferencesAreDiscoveredWithoutOpaqueScripting()
    {
        SongDocument song = new();
        ObjectId source = song.AllocateObjectId();
        ObjectId env = song.AllocateObjectId();
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "References")
        { Source = "pitchMultiplier < 2.0 ? 0 : -1" };
        instrument.ToneSpecifications.Add(new ToneSpecification
        { SourceId = source, VolumeEnvelopeId = env });
        song.Add(instrument);
        SongReferenceAnalysis refs = SongReferenceAnalyzer.Analyze(song);
        Assert.That(refs.HasOpaqueScriptReferences, Is.False);
        Assert.That(refs.References.Any(r => r.TargetId == source), Is.True);
        Assert.That(refs.References.Any(r => r.TargetId == env), Is.True);
    }
}
