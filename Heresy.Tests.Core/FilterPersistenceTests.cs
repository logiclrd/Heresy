using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterPersistenceTests
{
	private static readonly string JsonPath =
		Path.Combine(Path.GetTempPath(), "heresy-filter-persistence.hm.json");

	[Test]
	public void DefinitionStagesDefaultsAndIdentityHistoryRoundTripVersionOne()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		FilterDefinition definition = BuildDefinition(id);
		document.Add(definition);
		string json = SongDocumentJson.Serialize(document, JsonPath);
		JsonObject root = JsonNode.Parse(json)!.AsObject();
		Assert.That(root["version"]!.GetValue<int>(), Is.EqualTo(1));
		JsonObject entry = root["objects"]![id.Value.ToString()]!.AsObject();
		Assert.That(entry["type"]!.GetValue<string>(), Is.EqualTo("filter"));
		Assert.That(entry["stages"]!.AsArray().Count, Is.EqualTo(2));
		Assert.That(entry["nextStageId"]!.GetValue<int>(),
			Is.EqualTo(definition.NextStageIdForPersistence));

		SongDocument restored = SongDocumentJson.Deserialize(json, JsonPath);
		FilterDefinition copy = (FilterDefinition)restored.Objects[id];
		Assert.That(copy.Name, Is.EqualTo("Complex"));
		Assert.That(copy.Stages.Select(stage => stage.StageId),
			Is.EqualTo(new[] { 5, 2 }));
		Assert.That(copy.Stages.Select(stage => stage.Name),
			Is.EqualTo(new[] { "treble", "bass" }));
		Assert.That(copy.GetStage(5).Kind, Is.EqualTo(FilterKind.FirBandPass));
		Assert.That(copy.GetStage(5).DefaultParameters["TapCount"].NumericValue,
			Is.EqualTo(255));
		Assert.That(copy.GetStage(5).DefaultParameters["PhaseResponse"].ChoiceValue,
			Is.EqualTo("Minimum"));
		Assert.That(copy.GetStage(5).DefaultParameters["LowCutoffHz"].NumericValue,
			Is.EqualTo(1700));
		Assert.That(copy.GetStage(5).DefaultParameters["HighCutoffHz"].NumericValue,
			Is.EqualTo(4600));
		Assert.That(copy.NextStageIdForPersistence,
			Is.EqualTo(definition.NextStageIdForPersistence));
		Assert.That(copy.StageGenerationsForPersistence,
			Is.EquivalentTo(definition.StageGenerationsForPersistence));
		Assert.That(copy.AddStage("newStage", FilterKind.OnePole).StageId,
			Is.EqualTo(definition.NextStageIdForPersistence));
		Assert.That(restored.DocumentRevision, Is.Zero);
		Assert.That(restored.AudioRevision, Is.Zero);
	}

	[Test]
	public void SnapshotIsolatedFromSubsequentFilterDefaultEdits()
	{
		SongDocument song = new();
		ObjectId id = song.AllocateObjectId();
		FilterDefinition original = BuildDefinition(id);
		song.Add(original);
		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(song);
		FilterDefinition frozen = (FilterDefinition)snapshot.Document.Objects[id];
		Assert.That(frozen, Is.Not.SameAs(original));

		original.RenameStage(5, "renamedLive");
		original.SetStageDefaults(5, new[] {
			Pair("TapCount", FilterParameterValue.FromNumber(511)),
		});
		song.MarkChanged(affectsAudio: true);
		Assert.That(frozen.GetStage(5).Name, Is.EqualTo("treble"));
		Assert.That(frozen.GetStage(5).DefaultParameters["TapCount"].NumericValue,
			Is.EqualTo(255));
		Assert.That(snapshot.AudioRevision, Is.EqualTo(1));
		Assert.That(snapshot.DocumentRevision, Is.EqualTo(1));
		Assert.That(song.AudioRevision, Is.EqualTo(2));
		Assert.That(frozen.StageGenerationsForPersistence,
			Is.EquivalentTo(((FilterDefinition)snapshot.Document.Objects[id])
				.StageGenerationsForPersistence));
	}

	[Test]
	public void ConfiguredInstancesAndRecursiveChainsRoundTripByDefinitionId()
	{
		SongDocument song = new();
		ObjectId innerId = song.AllocateObjectId();
		FilterDefinition inner = BuildDefinition(innerId);
		song.Add(inner);
		ObjectId outerId = song.AllocateObjectId();
		FilterDefinition outer = new(outerId, "Outer");
		song.Add(outer);
		FilterInstance innerUse = new(inner);
		innerUse.SetStageParameters(5, new[] {
			Pair("TapCount", FilterParameterValue.FromNumber(127)),
			Pair("PhaseResponse", FilterParameterValue.FromChoice("maximum")),
		});
		FilterInstance outerUse = new(outer);
		outerUse.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));

		string instanceJson = SongDocumentJson.SerializeFilterInstance(innerUse);
		Assert.That(JsonNode.Parse(instanceJson)!["definitionId"]!.GetValue<uint>(),
			Is.EqualTo(innerId.Value));
		Assert.That(JsonNode.Parse(instanceJson)!["overrides"]!.AsArray()
			.Single()!["stageId"]!.GetValue<int>(), Is.EqualTo(5));
		string chainJson = SongDocumentJson.SerializeFilterChain(
			new FilterChain(new[] { innerUse, outerUse }));
		SongDocument snapshot = SongDocumentSnapshot.Create(song).Document;
		FilterInstance standalone = SongDocumentJson.DeserializeFilterInstance(
			instanceJson, snapshot);
		FilterChain restored = SongDocumentJson.DeserializeFilterChain(
			chainJson, snapshot);
		Assert.That(standalone.GetStageParameters(5)
			.GetParameter("PhaseResponse").ChoiceValue, Is.EqualTo("Maximum"));
		Assert.That(restored.Layers.Select(layer => layer.DefinitionId),
			Is.EqualTo(new[] { innerId, outerId }));
		Assert.That(restored.Layers[0].GetOverrides(5).Count, Is.EqualTo(2));
		Assert.That(restored.Layers[1].GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(3000));
		Assert.That(restored.Layers[0].GetStageParameters(5)
			.GetParameter("TapCount").NumericValue, Is.EqualTo(127));

		innerUse.SetStageParameter(5, "TapCount",
			FilterParameterValue.FromNumber(511));
		Assert.That(restored.Layers[0].GetStageParameters(5)
			.GetParameter("TapCount").NumericValue, Is.EqualTo(127));
	}

	[Test]
	public void InstanceRemapRebindsOverridesToImportedDefinition()
	{
		SongDocument source = new();
		ObjectId sourceId = source.AllocateObjectId();
		FilterDefinition filter = BuildDefinition(sourceId);
		source.Add(filter);
		FilterInstance original = new(filter);
		original.SetStageParameter(5, "TapCount",
			FilterParameterValue.FromNumber(127));
		string json = SongDocumentJson.SerializeFilterInstance(original);
		string chain = SongDocumentJson.SerializeFilterChain(
			new FilterChain(new[] { original, original }));
		SongDocument target = new();
		ObjectId importedId = (ObjectId)34U;
		target.Add(CloneThroughSerialization(filter, importedId));

		var mapping = new Dictionary<ObjectId, ObjectId> {
			[sourceId] = importedId,
		};
		FilterInstance imported = SongDocumentJson.DeserializeFilterInstance(
			json, target, mapping);
		FilterChain composed = SongDocumentJson.DeserializeFilterChain(
			chain, target, mapping);
		Assert.That(imported.DefinitionId, Is.EqualTo(importedId));
		Assert.That(imported.GetStageParameters(5).GetParameter("TapCount")
			.NumericValue, Is.EqualTo(127));
		Assert.That(composed.Layers.Select(layer => layer.DefinitionId),
			Is.EqualTo(new[] { importedId, importedId }));
		composed.Layers[0].SetStageParameter(5, "TapCount",
			FilterParameterValue.FromNumber(511));
		Assert.That(composed.Layers[1].GetStageParameters(5)
			.GetParameter("TapCount").NumericValue, Is.EqualTo(127));
	}

	[Test]
	public void MissingOrDeletedFilterReferencesAreExplicitlyRejected()
	{
		SongDocument source = new();
		ObjectId filterId = source.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Deleted");
		source.Add(definition);
		FilterInstance instance = new(definition);
		string json = SongDocumentJson.SerializeFilterInstance(instance);
		source.Remove(filterId);
		source.GetSectionRoot(SongTreeSection.Filters).Children.Add(
			new SongTreeObject("Deleted", filterId));
		SongDocument restored = SongDocumentJson.Deserialize(
			SongDocumentJson.Serialize(source, JsonPath), JsonPath);
		Assert.That(restored.Tombstones[filterId].Kind,
			Is.EqualTo(SongObjectKind.Filter));
		Assert.That(restored.DocumentRevision, Is.Zero);
		Assert.That(restored.AudioRevision, Is.Zero);
		Assert.That(() => SongDocumentJson.DeserializeFilterInstance(json, restored),
			Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing"));
	}

	[Test]
	public void InvalidFilterValuesAndTamperedStageMetadataRejectEntireDocument()
	{
		SongDocument doc = new();
		ObjectId id = doc.AllocateObjectId();
		doc.Add(BuildDefinition(id));
		JsonObject manifest = JsonNode.Parse(
			SongDocumentJson.Serialize(doc, JsonPath))!.AsObject();
		JsonObject filter = manifest["objects"]![id.Value.ToString()]!.AsObject();

		JsonObject wrong = manifest.DeepClone().AsObject();
		wrong["objects"]![id.Value.ToString()]!["stages"]![0]!["defaults"]!["TapCount"] = 4;
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(wrong.ToJsonString(), JsonPath));

		JsonObject duplicate = manifest.DeepClone().AsObject();
		duplicate["objects"]![id.Value.ToString()]!["stages"]![1]!["name"] = "TREBLE";
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(duplicate.ToJsonString(), JsonPath));

		JsonObject brokenAllocator = manifest.DeepClone().AsObject();
		brokenAllocator["objects"]![id.Value.ToString()]!["nextStageId"] = 1;
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(brokenAllocator.ToJsonString(), JsonPath));

		FilterInstance instance = new((FilterDefinition)doc.Objects[id]);
		string instanceJson = SongDocumentJson.SerializeFilterInstance(instance);
		JsonObject missing = JsonNode.Parse(instanceJson)!.AsObject();
		missing["definitionId"] = 999;
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.DeserializeFilterInstance(missing.ToJsonString(), doc));

		JsonObject malformed = JsonNode.Parse(instanceJson)!.AsObject();
		malformed["overrides"] = new JsonArray {
			new JsonObject {
				["stageId"] = 5,
				["kind"] = "FirBandPass",
				["generation"] = 0,
				["parameters"] = new JsonObject {
					["TapCount"] = 4,
				},
			},
		};
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.DeserializeFilterInstance(malformed.ToJsonString(), doc));
	}

	[Test]
	public void JsonAndZipSongStorageRoundTripDefinitionsWithoutAssets()
	{
		string directory = Path.Combine(Path.GetTempPath(),
			$"heresy-filter-roundtrip-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			SongDocument doc = new();
			ObjectId id = doc.AllocateObjectId();
			doc.Add(BuildDefinition(id));
			foreach (string name in new[] { "filter.hm.json", "filter.hm" })
			{
				string path = Path.Combine(directory, name);
				SongDocumentStorage.Save(path, doc);
				SongDocument loaded = SongDocumentStorage.Load(path);
				FilterDefinition restored = (FilterDefinition)loaded.Objects[id];
				Assert.That(restored.GetStage(5).DefaultParameters["TapCount"]
					.NumericValue, Is.EqualTo(255));
				Assert.That(restored.NextStageIdForPersistence,
					Is.EqualTo(((FilterDefinition)doc.Objects[id])
						.NextStageIdForPersistence));
				Assert.That(loaded.DocumentRevision, Is.Zero);
				Assert.That(loaded.AudioRevision, Is.Zero);
			}
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static FilterDefinition BuildDefinition(ObjectId id)
	{
		FilterDefinition definition = new(id, "Complex", new[] {
			new FilterStageDefinition(2, "bass", FilterKind.OnePole),
			new FilterStageDefinition(5, "treble", FilterKind.FirBandPass),
		});
		definition.SetStageDefaults(5, new[] {
			Pair("LowCutoffHz", FilterParameterValue.FromNumber(1700)),
			Pair("HighCutoffHz", FilterParameterValue.FromNumber(4600)),
			Pair("TapCount", FilterParameterValue.FromNumber(255)),
			Pair("PhaseResponse", FilterParameterValue.FromChoice("Minimum")),
		});
		definition.MoveStage(5, 0);
		definition.ChangeStageKind(2, FilterKind.BiquadLowPass);
		definition.ChangeStageKind(2, FilterKind.OnePole);
		int oldId = definition.AddStage("temporary", FilterKind.SvfNotch).StageId;
		definition.RemoveStage(oldId);
		return definition;
	}

	private static FilterDefinition CloneThroughSerialization(
		FilterDefinition definition, ObjectId newId)
	{
		SongDocument source = new();
		source.Add(definition);
		JsonObject json = JsonNode.Parse(
			SongDocumentJson.Serialize(source, JsonPath))!.AsObject();
		JsonObject objects = json["objects"]!.AsObject();
		JsonNode node = objects[definition.Id.Value.ToString()]!.DeepClone();
		objects.Clear();
		objects[newId.Value.ToString()] = node;
		json["nextObjectId"] = newId.Value + 1;
		// Object ID remapping of references is handled by the caller; this
		// independent cloning helper needs no actual source tree placements.
		JsonArray tree = json["tree"]!["children"]!.AsArray();
		foreach (JsonNode? section in tree)
			section!["children"]!.AsArray().Clear();
		tree[3]!["children"]!.AsArray().Add(new JsonObject {
			["type"] = "object", ["name"] = definition.Name,
			["objectId"] = newId.Value,
		});
		return (FilterDefinition)SongDocumentJson.Deserialize(
			json.ToJsonString(), JsonPath).Objects[newId];
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
