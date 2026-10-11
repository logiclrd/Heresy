using System;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmFilterAuthoringTests
{
	[Test]
	public void UnassignedFilterHasAudioInputOnlyAndPersistedNonnegativeTail()
	{
		DocumentWorkspace work = new();
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(work, fm, 220, 80);
		FmFilterNode node = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(node.Id, Is.EqualTo(id));
		Assert.That(node.Configuration, Is.Null);
		Assert.That(node.GetPorts().Count, Is.EqualTo(1));
		Assert.That(FmSynthConnectionPorts.GetPorts(node,
			new FmSynthLayoutRect(0, 0, 170, 72)).Length, Is.EqualTo(2));

		FmSynthDocumentEditor.ConnectNodes(work, fm, 0, id, 0);
		node = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(node.AudioInputNodeId, Is.EqualTo(0));
		FmSynthDocumentEditor.UpdateNode(work, fm, node.WithTail(0.35));
		Assert.Throws<ArgumentOutOfRangeException>(() => node.WithTail(-1));

		string json = SongDocumentJson.Serialize(work.Document, "/tmp/fm-filter.hm.json");
		SongDocument loaded = SongDocumentJson.Deserialize(json,
			"/tmp/fm-filter.hm.json");
		FmFilterNode roundTrip = ((FmSynthDefinition)loaded.Objects[fm.Id])
			.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(roundTrip.Configuration, Is.Null);
		Assert.That(roundTrip.AudioInputNodeId, Is.EqualTo(0));
		Assert.That(roundTrip.TailSeconds, Is.EqualTo(0.35));
	}

	[Test]
	public void DynamicModulationPortsFollowStableStageIdsAcrossReorderAndRename()
	{
		DocumentWorkspace work = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(work, "Formants");
		FilterDocumentEditor.AddStage(work, definition, FilterKind.BiquadPeaking);
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(work, fm, 200, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			work, fm, id, new FilterInstance(definition));
		FmFilterNode node = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(node.GetPorts().Select(p => p.ParameterName),
			Is.EqualTo(new[] { "Input", "CutoffHz",
				"CutoffHz", "Q", "GainDb" }));
		Assert.That(node.GetPorts().Count, Is.EqualTo(5));

		FmSynthDocumentEditor.ConnectNodes(work, fm, 0, id, 0);
		FmSynthDocumentEditor.ConnectNodes(work, fm, 0, id, 4);
		node = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(node.Connections.Single().StageId, Is.EqualTo(2));
		Assert.That(node.Connections.Single().ParameterName, Is.EqualTo("GainDb"));
		Assert.That(node.InputNodeIds, Is.EqualTo(new[] { 0, 0 }));

		definition.MoveStage(2, 0);
		definition.RenameStage(2, "Peak");
		node = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(node.GetPorts()[3].ParameterName, Is.EqualTo("GainDb"));
		Assert.That(node.GetPorts()[3].SourceNodeId, Is.EqualTo(0));
		Assert.That(node.GetPorts()[4].SourceNodeId, Is.Null);

		// A type change advances generation and cannot quietly redirect
		// the old GainDb wire to a new parameter at the same ordinal.
		definition.ChangeStageKind(2, FilterKind.SvfNotch);
		Assert.That(node.StaleConnections.Count, Is.EqualTo(1));
		Assert.That(node.GetPorts().All(p => p.Index == 0
			|| p.SourceNodeId is null), Is.True);
		Assert.That(node.InputNodeIds, Is.EqualTo(new[] { 0 }));
	}

	[Test]
	public void ConfiguredOverridesConnectionsAndTailSurviveJsonAndSnapshot()
	{
		DocumentWorkspace work = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(work, "Sweep");
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(work, "Sweep FM");
		int id = FmSynthDocumentEditor.AddFilterNode(work, fm, 200, 100);
		FilterInstance configured = new(filter);
		configured.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2350));
		FmSynthDocumentEditor.SetFilterConfiguration(work, fm, id, configured);
		FmSynthDocumentEditor.ConnectNodes(work, fm, 0, id, 0);
		FmSynthDocumentEditor.ConnectNodes(work, fm, 0, id, 1);
		FmFilterNode live = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		FmSynthDocumentEditor.UpdateNode(work, fm, live.WithTail(0.8));
		FmSynthDocumentEditor.SetOutputNode(work, fm, id);

		SongDocument snapshot = SongDocumentSnapshot.Create(work.Document).Document;
		string json = SongDocumentJson.Serialize(work.Document, "/tmp/fm-filter.hm.json");
		SongDocument loaded = SongDocumentJson.Deserialize(json,
			"/tmp/fm-filter.hm.json");
		foreach (SongDocument document in new[] { snapshot, loaded })
		{
			FmFilterNode copy = ((FmSynthDefinition)document.Objects[fm.Id])
				.Graph.Nodes.OfType<FmFilterNode>().Single();
			Assert.That(copy.Configuration!.DefinitionId, Is.EqualTo(filter.Id));
			Assert.That(copy.Configuration.GetOverrides(1)["CutoffHz"].NumericValue,
				Is.EqualTo(2350));
			Assert.That(copy.GetPorts()[1].SourceNodeId, Is.EqualTo(0));
			Assert.That(copy.AudioInputNodeId, Is.EqualTo(0));
			Assert.That(copy.TailSeconds, Is.EqualTo(0.8));
			Assert.That(copy.Configuration.Definition,
				Is.SameAs(document.Objects[filter.Id]));
		}
		filter.SetStageDefaults(1, new[] {
			new System.Collections.Generic.KeyValuePair<string, FilterParameterValue>(
				"CutoffHz", FilterParameterValue.FromNumber(4400)),
		});
		FmFilterNode frozen = ((FmSynthDefinition)snapshot.Objects[fm.Id])
			.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(frozen.Configuration!.Definition.Stages[0]
			.DefaultParameters["CutoffHz"].NumericValue, Is.Not.EqualTo(4400));
	}


	[Test]
	public void ImportRemapsDependentFilterWithoutMutatingOriginalConfiguration()
	{
		DocumentWorkspace sourceWork = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(
			sourceWork, "Source Filter");
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(
			sourceWork, "Modulated FM");
		int id = FmSynthDocumentEditor.AddFilterNode(
			sourceWork, fm, 190, 80);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3333));
		FmSynthDocumentEditor.SetFilterConfiguration(sourceWork, fm, id, use);
		FmSynthDocumentEditor.ConnectNodes(sourceWork, fm, 0, id, 0);
		FmSynthDocumentEditor.ConnectNodes(sourceWork, fm, 0, id, 1);
		SongFmSynthImportSource source = new(
			"source.hm", sourceWork.Document, new[] { fm });
		DocumentWorkspace target = new();
		_ = FilterDocumentEditor.CreateFilter(target, "Unrelated");
		FmSynthDefinition imported = FmSynthDocumentEditor.ImportFromSong(
			target, source, new[] { fm.Id }).Single();
		FmFilterNode clone = imported.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(clone.Configuration, Is.Not.Null);
		Assert.That(clone.Configuration!.DefinitionId,
			Is.Not.EqualTo(definition.Id));
		Assert.That(clone.Configuration.Definition,
			Is.SameAs(target.Document.Objects[clone.Configuration.DefinitionId]));
		Assert.That(clone.Configuration.GetOverrides(1)["CutoffHz"].NumericValue,
			Is.EqualTo(3333));
		Assert.That(clone.GetPorts()[1].SourceNodeId, Is.EqualTo(0));
		Assert.That(sourceWork.Document.Objects[definition.Id],
			Is.SameAs(definition));
	}

	[Test]
	public void DeletingModulationSourceDisconnectsItWithoutDestroyingFilter()
	{
		DocumentWorkspace work = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(work, "Cutoff");
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int filterNode = FmSynthDocumentEditor.AddFilterNode(work, fm, 200, 80);
		int source = FmSynthDocumentEditor.AddConstantNode(work, fm, 1, 100, 80);
		FmSynthDocumentEditor.SetFilterConfiguration(
			work, fm, filterNode, new FilterInstance(filter));
		FmSynthDocumentEditor.ConnectNodes(work, fm, source, filterNode, 1);
		// Even an invalidated binding must be purged if its source is
		// explicitly deleted, rather than becoming a ghost wire in JSON.
		filter.ChangeStageKind(1, FilterKind.AllPass);
		Assert.That(fm.Graph.Nodes.OfType<FmFilterNode>().Single()
			.StaleConnections.Count, Is.EqualTo(1));
		FmSynthDocumentEditor.RemoveNode(work, fm, source);
		FmFilterNode remaining = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(remaining.Connections, Is.Empty);
		Assert.That(remaining.GetPorts()[1].SourceNodeId, Is.Null);
		Assert.That(remaining.Configuration!.DefinitionId, Is.EqualTo(filter.Id));
	}
}
