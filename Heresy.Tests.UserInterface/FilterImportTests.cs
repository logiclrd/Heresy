using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterImportTests
{
	[Test]
	public void LoadFilterImportSourceListsDefinitionsInStableIdOrder()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"heresy-filter-import-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		try
		{
			SongDocument source = new();
			source.Add(new FilterDefinition((ObjectId)15U, "Last"));
			source.Add(new FilterDefinition((ObjectId)4U, "First"));
			string file = Path.Combine(folder, "original.hm");
			SongDocumentStorage.Save(file, source);
			SongFilterImportSource loaded = FilterDocumentImport.LoadImportSource(file);
			Assert.That(loaded.Filters.Select(f => f.Id),
				Is.EqualTo(new[] { (ObjectId)4U, (ObjectId)15U }));
			Assert.That(loaded.Filters.Select(f => f.Name),
				Is.EqualTo(new[] { "First", "Last" }));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Test]
	public void ImportsSelectedDefinitionsWithoutUnrelatedOnesAndRemapsInstances()
	{
		SongDocument sourceDoc = new();
		ObjectId innerId = sourceDoc.AllocateObjectId();
		FilterDefinition inner = new(innerId, "Inner", new[] {
			new FilterStageDefinition(8, "stage8", FilterKind.FirLowPass),
			new FilterStageDefinition(12, "stage12", FilterKind.SvfNotch),
		});
		inner.SetStageDefaults(8, new[] {
			Pair("TapCount", FilterParameterValue.FromNumber(255)),
		});
		int removed = inner.AddStage("retired", FilterKind.BiquadHighShelf).StageId;
		inner.RemoveStage(removed);
		sourceDoc.Add(inner);

		ObjectId unusedId = sourceDoc.AllocateObjectId();
		sourceDoc.Add(new FilterDefinition(unusedId, "Do not import"));
		ObjectId outerId = sourceDoc.AllocateObjectId();
		FilterDefinition outer = new(outerId, "Outer");
		sourceDoc.Add(outer);

		FilterInstance innerUse = new(inner);
		innerUse.SetStageParameter(8, "TapCount",
			FilterParameterValue.FromNumber(511));
		FilterInstance outerUse = new(outer);
		outerUse.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));
		string chainJson = SongDocumentJson.SerializeFilterChain(
			new FilterChain(new[] { innerUse, outerUse }));

		SongFilterImportSource input =
			new("source.hm.json", sourceDoc, new[] { inner, outer });
		DocumentWorkspace target = new();
		for (int i = 0; i < 40; i++)
			target.Document.AllocateObjectId();
		IReadOnlyDictionary<ObjectId, ObjectId> idMap = FilterDocumentImport.ImportFromSong(
			target, input, new[] { outerId, innerId });

		Assert.That(idMap.Count, Is.EqualTo(2));
		Assert.That(idMap.Keys, Is.EquivalentTo(new[] { innerId, outerId }));
		Assert.That(idMap.Values.All(id => id != innerId && id != outerId), Is.True);
		Assert.That(target.Document.Objects.Count, Is.EqualTo(2));
		Assert.That(target.Document.Objects.Values.OfType<FilterDefinition>()
			.Select(f => f.Name), Is.EquivalentTo(new[] { "Inner", "Outer" }));

		FilterDefinition importedInner = (FilterDefinition)target.Document.Objects[idMap[innerId]];
		Assert.That(importedInner, Is.Not.SameAs(inner));
		Assert.That(importedInner.Stages.Select(stage => stage.StageId),
			Is.EqualTo(new[] { 8, 12 }));
		Assert.That(importedInner.StageGenerationsForPersistence,
			Is.EquivalentTo(inner.StageGenerationsForPersistence));
		Assert.That(importedInner.NextStageIdForPersistence,
			Is.EqualTo(inner.NextStageIdForPersistence));

		FilterChain rebound = SongDocumentJson.DeserializeFilterChain(
			chainJson, target.Document, idMap);
		Assert.That(rebound.Layers.Select(layer => layer.DefinitionId),
			Is.EqualTo(new[] { idMap[innerId], idMap[outerId] }));
		Assert.That(rebound.Layers[0].GetStageParameters(8)
			.GetParameter("TapCount").NumericValue, Is.EqualTo(511));
		Assert.That(rebound.Layers[1].GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(3000));

		inner.SetStageDefaults(8, new[] {
			Pair("TapCount", FilterParameterValue.FromNumber(127)),
		});
		Assert.That(importedInner.GetStage(8).DefaultParameters["TapCount"]
			.NumericValue, Is.EqualTo(255));
		Assert.That(target.Document.GetSectionRoot(SongTreeSection.Filters).Children.Count,
			Is.EqualTo(2));
		Assert.That(target.Document.AudioRevision, Is.EqualTo(2));
	}

	[Test]
	public void MissingFilterSelectionFailsBeforeAnyTargetMutation()
	{
		SongDocument source = new();
		ObjectId id = source.AllocateObjectId();
		FilterDefinition definition = new(id, "Only");
		source.Add(definition);
		DocumentWorkspace target = new();
		uint next = target.Document.AllocateObjectId().Value;
		Assert.That(next, Is.EqualTo(1U));
		Assert.Throws<InvalidOperationException>(() =>
			FilterDocumentImport.ImportFromSong(
				target, new SongFilterImportSource("source.hm", source, new[] { definition }),
				new[] { id, (ObjectId)999U }));
		Assert.That(target.Document.Objects, Is.Empty);
		Assert.That(target.Document.DocumentRevision, Is.Zero);
		Assert.That(target.Document.AudioRevision, Is.Zero);
		Assert.That(target.Document.AllocateObjectId().Value, Is.EqualTo(2U));
	}

	[Test]
	public void EmptySelectionDoesNotChangeRevisionsOrIds()
	{
		SongDocument source = new();
		DocumentWorkspace workspace = new();
		IReadOnlyDictionary<ObjectId, ObjectId> map = FilterDocumentImport.ImportFromSong(
			workspace,
			new SongFilterImportSource("source.hm", source, Array.Empty<FilterDefinition>()),
			Array.Empty<ObjectId>());
		Assert.That(map, Is.Empty);
		Assert.That(workspace.Document.AllocateObjectId(), Is.EqualTo((ObjectId)1U));
		Assert.That(workspace.Document.DocumentRevision, Is.Zero);
		Assert.That(workspace.Document.AudioRevision, Is.Zero);
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
