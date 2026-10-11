using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FilterTreeMigrationTests
{
	private static readonly string JsonPath =
		Path.Combine(Path.GetTempPath(), "filter-tree-migration.hm.json");

	[Test]
	public void FiveRootSongPromotesEmptyFiltersPaneWithoutTouchingOldFoldersOrIds()
	{
		SongDocument document = new();
		document.Root.Name = "Untitled custom root";
		ObjectId instrumentId = document.AllocateObjectId();
		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new InstrumentDefinition(instrumentId, "Keys"));
		document.Add(new NodeEnvelopeDefinition(envelopeId, "Volume"));

		SongTreeFolder instruments =
			document.GetSectionRoot(SongTreeSection.Instruments);
		SongTreeObject instrument = (SongTreeObject)instruments.Children.Single();
		instruments.Children.Clear();
		SongTreeFolder instrumentsFolder = new("Nested Instruments");
		instrumentsFolder.Children.Add(instrument);
		instruments.Children.Add(instrumentsFolder);

		SongTreeFolder envelopes =
			document.GetSectionRoot(SongTreeSection.Envelopes);
		SongTreeObject envelope = (SongTreeObject)envelopes.Children.Single();
		envelopes.Children.Clear();
		SongTreeFolder envelopesFolder = new("Nested Envelopes");
		envelopesFolder.Children.Add(envelope);
		envelopes.Children.Add(envelopesFolder);

		JsonObject json = JsonNode.Parse(
			SongDocumentJson.Serialize(document, JsonPath))!.AsObject();
		JsonArray children = json["tree"]!["children"]!.AsArray();
		Assert.That(children.Count, Is.EqualTo(6));
		children.RemoveAt(3); // Historical five-root layout had no Filters.

		SongDocument upgraded = SongDocumentJson.Deserialize(
			json.ToJsonString(), JsonPath);
		Assert.That(upgraded.Root.Name, Is.EqualTo("Untitled custom root"));
		Assert.That(upgraded.Root.Children.Select(node => node.Name),
			Is.EqualTo(new[] { "Sequences", "Patterns", "Patches",
				"Filters", "Envelopes", "Instruments" }));
		Assert.That(upgraded.GetSectionRoot(SongTreeSection.Filters).Children,
			Is.Empty);
		Assert.That(((SongTreeObject)((SongTreeFolder)upgraded
			.GetSectionRoot(SongTreeSection.Instruments).Children.Single())
			.Children.Single()).ObjectId, Is.EqualTo(instrumentId));
		Assert.That(((SongTreeObject)((SongTreeFolder)upgraded
			.GetSectionRoot(SongTreeSection.Envelopes).Children.Single())
			.Children.Single()).ObjectId, Is.EqualTo(envelopeId));
		Assert.That(upgraded.Objects.Keys,
			Is.EquivalentTo(new[] { instrumentId, envelopeId }));
		Assert.That(upgraded.DocumentRevision, Is.Zero);
		Assert.That(upgraded.AudioRevision, Is.Zero);
		Assert.That(upgraded.AllocateObjectId(), Is.EqualTo((ObjectId)3U));

		JsonObject resaved = JsonNode.Parse(
			SongDocumentJson.Serialize(upgraded, JsonPath))!.AsObject();
		Assert.That(resaved["version"]!.GetValue<int>(), Is.EqualTo(1));
		Assert.That(resaved["tree"]!["children"]!.AsArray()
			.Select(node => node!["name"]!.GetValue<string>()),
			Is.EqualTo(new[] { "Sequences", "Patterns", "Patches",
				"Filters", "Envelopes", "Instruments" }));
	}

	[Test]
	public void FiveRootMigrationDoesNotDiscardReferencedTombstones()
	{
		SongDocument document = new();
		ObjectId deletedId = document.AllocateObjectId();
		document.Add(new NodeEnvelopeDefinition(deletedId, "Missing"));
		document.Remove(deletedId);
		document.GetSectionRoot(SongTreeSection.Envelopes)
			.Children.Add(new SongTreeObject("Missing", deletedId));

		JsonObject json = JsonNode.Parse(
			SongDocumentJson.Serialize(document, JsonPath))!.AsObject();
		json["tree"]!["children"]!.AsArray().RemoveAt(3);
		SongDocument upgraded = SongDocumentJson.Deserialize(
			json.ToJsonString(), JsonPath);

		Assert.That(upgraded.Tombstones.ContainsKey(deletedId), Is.True);
		Assert.That(((SongTreeObject)upgraded
			.GetSectionRoot(SongTreeSection.Envelopes).Children.Single())
			.ObjectId, Is.EqualTo(deletedId));
		Assert.That(upgraded.DocumentRevision, Is.Zero);
		Assert.That(upgraded.AudioRevision, Is.Zero);
	}

	[Test]
	public void CorruptFiveAndSixRootLayoutsAreRejectedWithoutGuessing()
	{
		SongDocument document = new();
		string serialized = SongDocumentJson.Serialize(document, JsonPath);

		JsonObject six = JsonNode.Parse(serialized)!.AsObject();
		JsonArray sixChildren = six["tree"]!["children"]!.AsArray();
		JsonNode misplaced = sixChildren[3]!.DeepClone();
		sixChildren[3] = sixChildren[4]!.DeepClone();
		sixChildren[4] = misplaced;
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(six.ToJsonString(), JsonPath));

		JsonObject five = JsonNode.Parse(serialized)!.AsObject();
		JsonArray fiveChildren = five["tree"]!["children"]!.AsArray();
		fiveChildren.RemoveAt(3);
		fiveChildren[2]!["name"] = "Old Samples";
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(five.ToJsonString(), JsonPath));

		JsonObject unknown = JsonNode.Parse(serialized)!.AsObject();
		unknown["tree"]!["children"]!.AsArray().RemoveAt(0);
		unknown["tree"]!["children"]!.AsArray().RemoveAt(0);
		Assert.Throws<InvalidDataException>(() =>
			SongDocumentJson.Deserialize(unknown.ToJsonString(), JsonPath));
	}

	[Test]
	public void FilterTombstonesUseTheirOwnKindAndRemainInFiltersPane()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		document.Add(new FilterDefinition(filterId, "To remove"));
		document.Remove(filterId);
		document.GetSectionRoot(SongTreeSection.Filters)
			.Children.Add(new SongTreeObject("To remove", filterId));
		JsonObject saved = JsonNode.Parse(
			SongDocumentJson.Serialize(document, JsonPath))!.AsObject();
		Assert.That(saved["tombstones"]![filterId.Value.ToString()]!["kind"]!
			.GetValue<string>(), Is.EqualTo("filter"));

		SongDocument loaded = SongDocumentJson.Deserialize(
			saved.ToJsonString(), JsonPath);
		Assert.That(loaded.Tombstones[filterId].Kind, Is.EqualTo(SongObjectKind.Filter));
		Assert.That(((SongTreeObject)loaded
			.GetSectionRoot(SongTreeSection.Filters).Children.Single()).ObjectId,
			Is.EqualTo(filterId));
	}
}
