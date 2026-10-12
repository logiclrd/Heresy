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
        song.Add(new NodeEnvelopeDefinition(envelope, "Volume"));
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
    public void NnaHierarchySurvivesDataAndScriptDocumentAndSnapshotRoundTrips()
    {
        SongDocument document = new();
        ObjectId dataId = document.AllocateObjectId();
        InstrumentDefinition data = new(dataId, "Data NNA")
        {
            NnaOverride = InstrumentNnaOverride.Fade,
            NnaFadeDuration = TimeSpan.FromMilliseconds(77),
        };
        data.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = (ObjectId)91001U,
            Nna = new InstrumentNnaSetting(InstrumentNnaAction.Off),
        });
        data.ToneTable.Add(0);
        document.Add(data);
        ObjectId scriptId = document.AllocateObjectId();
        ScriptInstrumentDefinition script = new(scriptId, "Script NNA")
        {
            Source = "0",
            NnaExpression = "pitchMultiplier < 2 ? InstrumentNnaAction.Inherit : InstrumentNnaAction.Continue",
            NnaFadeDuration = TimeSpan.FromMilliseconds(57),
        };
        script.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = dataId,
            Nna = InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(15)),
        });
        document.Add(script);
        string path = Path.Combine(Path.GetTempPath(),
            "heresy-nna-" + Guid.NewGuid().ToString("N") + ".hm.json");
        SongDocument restoredDocument = SongDocumentJson.Deserialize(
            SongDocumentJson.Serialize(document, path), path);
        InstrumentDefinition restoredData =
            (InstrumentDefinition)restoredDocument.Objects[dataId];
        ScriptInstrumentDefinition restoredScript =
            (ScriptInstrumentDefinition)restoredDocument.Objects[scriptId];
        Assert.That(restoredData.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Fade));
        Assert.That(restoredData.NnaFadeDuration, Is.EqualTo(TimeSpan.FromMilliseconds(77)));
        Assert.That(restoredData.ToneSpecifications[0].Nna,
            Is.EqualTo(new InstrumentNnaSetting(InstrumentNnaAction.Off)));
        Assert.That(restoredScript.NnaExpression, Is.EqualTo(script.NnaExpression));
        Assert.That(restoredScript.NnaFadeDuration, Is.EqualTo(TimeSpan.FromMilliseconds(57)));
        Assert.That(restoredScript.ToneSpecifications[0].Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(15))));

        SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(document);
        data.NnaOverride = InstrumentNnaOverride.Cut;
        script.NnaExpression = "InstrumentNnaAction.Cut";
        Assert.That(((InstrumentDefinition)frozen.Document.Objects[dataId]).NnaOverride,
            Is.EqualTo(InstrumentNnaOverride.Fade));
        Assert.That(((ScriptInstrumentDefinition)frozen.Document.Objects[scriptId]).NnaExpression,
            Is.EqualTo(restoredScript.NnaExpression));

        var tree = System.Text.Json.Nodes.JsonNode.Parse(
            SongDocumentJson.Serialize(document, path))!.AsObject();
        var objects = tree["objects"]!.AsObject();
        var d = objects[dataId.Value.ToString()]!.AsObject();
        d.Remove("nnaOverride");
        d.Remove("nnaFadeDuration");
        d["toneSpecifications"]![0]!.AsObject().Remove("nna");
        var s = objects[scriptId.Value.ToString()]!.AsObject();
        s.Remove("nnaExpression");
        s.Remove("nnaFadeDuration");
        s["toneSpecifications"]![0]!.AsObject().Remove("nna");
        SongDocument legacy = SongDocumentJson.Deserialize(tree.ToJsonString(), path);
        Assert.That(((InstrumentDefinition)legacy.Objects[dataId]).NnaOverride,
            Is.EqualTo(InstrumentNnaOverride.UseToneNna));
        Assert.That(((InstrumentDefinition)legacy.Objects[dataId])
            .ToneSpecifications[0].Nna.Action, Is.EqualTo(InstrumentNnaAction.Inherit));
        Assert.That(((ScriptInstrumentDefinition)legacy.Objects[scriptId]).NnaExpression,
            Is.EqualTo("InstrumentNnaAction.Inherit"));
    }

    [Test]
    public void InvalidSavedNnaIsRejectedBeforeSnapshotCanRender()
    {
        SongDocument document = new();
        ObjectId id = document.AllocateObjectId();
        InstrumentDefinition instrument = new(id, "Invalid")
        {
            NnaOverride = InstrumentNnaOverride.Fade,
        };
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = (ObjectId)12345U,
            Nna = InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(25)),
        });
        document.Add(instrument);
        string path = Path.Combine(Path.GetTempPath(), "nna.hm.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            SongDocumentJson.Serialize(document, path))!.AsObject();
        var entry = manifest["objects"]![id.Value.ToString()]!.AsObject();
        entry["nnaOverride"] = 999;
        Assert.Throws<InvalidDataException>(() =>
            SongDocumentJson.Deserialize(manifest.ToJsonString(), path));
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
