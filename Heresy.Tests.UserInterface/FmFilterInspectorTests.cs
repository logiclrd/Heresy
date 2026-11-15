using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmFilterInspectorTests
{
	[AvaloniaTest]
	public void FilterSelectorAndInstanceEditorAppearOnlyWhenDefinitionIsSelected()
	{
		DocumentWorkspace work = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(
			work, "Formant EQ");
		FmSynthDefinition fm = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int filterNode = FmSynthDocumentEditor.AddFilterNode(
			work, fm, 210, 80);
		FmSynthEditorControl editor = new(
			work, fm, () => { }, _ => { });
		typeof(FmSynthEditorControl)
			.GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(editor, new object[] { filterNode });
		StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;

		int selectorLabel = inspector.Children
			.Select((child, index) => (child, index))
			.Single(pair => pair.child is TextBlock text
				&& text.Text == "Shared Filter definition").index;
		StackPanel selectorRow =
			( StackPanel )inspector.Children[selectorLabel + 1];
		ComboBox selector = (ComboBox)selectorRow.Children[0];
		Button definitionEditor = (Button)selectorRow.Children[1];
		Assert.That(selector.SelectedItem, Is.Null);
		Assert.That(definitionEditor.IsEnabled, Is.False);
		Assert.That(inspector.Children.OfType<FilterInstanceEditorControl>(),
			Is.Empty);

		// New... (italic) and (None) precede live Filter definitions.
		object[] choices = selector.Items.Cast<object>().ToArray();
		Assert.That(choices.Length, Is.EqualTo(3));
		Assert.That(((TextBlock)((ComboBoxItem)choices[0]).Content!).Text,
			Is.EqualTo("New..."));
		Assert.That(((ComboBoxItem)choices[1]).Content, Is.EqualTo("(None)"));
		selector.SelectedItem = choices[2];

		FmFilterNode live = fm.Graph.Nodes.OfType<FmFilterNode>().Single();
		Assert.That(live.Configuration!.DefinitionId, Is.EqualTo(definition.Id));
		Assert.That(inspector.Children.OfType<FilterInstanceEditorControl>().Count(),
			Is.EqualTo(1));
		Assert.That(inspector.Children
			.OfType<TextBlock>().Any(text =>
				text.Text == "Tail (seconds, nonnegative)"), Is.True);
		// A real FM Filter already processes sound and Tail already preserves
		// residual samples. Keep the contextual inspector explanation
		// synchronized with the tested renderer and live adoption semantics.
		string explanation = inspector.Children.OfType<TextBlock>()
			.Single(text => text.Text?.StartsWith(
				"Audio enters the Input port.", StringComparison.Ordinal) == true)
			.Text!;
		Assert.Multiple(() =>
		{
			Assert.That(explanation, Does.Contain("Filters process audio"));
			Assert.That(explanation, Does.Contain("Tail can preserve residual"));
			Assert.That(explanation, Does.Contain("older graph"));
			Assert.That(explanation, Does.Not.Contain("not yet applied"));
			Assert.That(explanation, Does.Not.Contain("passes audio unchanged"));
		});
	}

	[AvaloniaTest]
	public void SharedFilterKindCommitRefreshesVisibleInspectorWithoutReselectingNode()
	{
		DocumentWorkspace work = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(work, "Shared");
		FilterStageDefinition first = definition.GetStage(1);
		FilterDocumentEditor.ApplyStage(work, definition, 1, first,
			"voice", FilterKind.BiquadPeaking,
			new FilterStageDefinition(1, "voice", FilterKind.BiquadPeaking)
				.DefaultParameters);
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int nodeId = FmSynthDocumentEditor.AddFilterNode(work, synth, 110, 90);
		FmSynthDocumentEditor.SetFilterConfiguration(
			work, synth, nodeId, new FilterInstance(definition));
		FmSynthEditorControl editor = SynthEditor(work, synth, nodeId);
		StackPanel inspector = Inspector(editor);
		FilterInstanceEditorControl existing =
			inspector.Children.OfType<FilterInstanceEditorControl>().Single();
		ListBox list = InstanceField<ListBox>(existing, "_stages");
		Assert.That(((ListBoxItem)list.SelectedItem!).Content!.ToString(),
			Does.Contain("BiquadPeaking"));
		uint beforeRefresh = work.Document.AudioRevision;

		FilterStageDefinition prior = definition.GetStage(1);
		FilterDocumentEditor.ApplyStage(work, definition, 1, prior,
			"notch", FilterKind.SvfNotch,
			new FilterStageDefinition(1, "notch", FilterKind.SvfNotch)
				.DefaultParameters);
		uint committedRevision = work.Document.AudioRevision;
		Assert.That(committedRevision, Is.GreaterThan(beforeRefresh));
		NotifySharedFilterChanged(editor, nodeId);

		Assert.That(inspector.Children.OfType<FilterInstanceEditorControl>()
			.Single(), Is.SameAs(existing),
			"refresh must not replace the per-use editor and discard drafts");
		Assert.That(((ListBoxItem)list.SelectedItem!).Content!.ToString(),
			Does.Contain("notch — SvfNotch"));
		Assert.That(InstanceField<TextBlock>(existing, "_stageLabel").Text,
			Does.Contain("notch — SvfNotch"));
		Assert.That(InstanceField<Dictionary<string, Control>>(existing, "_editors")
			.Keys, Is.EquivalentTo(FilterCatalogue.GetParameters(FilterKind.SvfNotch)
				.Select(x => x.Name)),
			"the selected stage's parameter controls must reflect its new kind");
		Assert.That(StageSummary(inspector).Children.OfType<TextBlock>()
			.Any(t => t.Text?.Contains("notch (stage #1)") == true), Is.True);
		Assert.That(work.Document.AudioRevision, Is.EqualTo(committedRevision),
			"presentation refresh must not publish another audio edit");
	}

	[AvaloniaTest]
	public void SharedFilterStageMutationsRefreshOrderAndPreserveStableSelection()
	{
		DocumentWorkspace work = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(work, "Stages");
		FilterStageDefinition second = FilterDocumentEditor.AddStage(
			work, definition, FilterKind.BiquadPeaking);
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(work, synth, 100, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			work, synth, id, new FilterInstance(definition));
		FmSynthEditorControl editor = SynthEditor(work, synth, id);
		FilterInstanceEditorControl instance = Inspector(editor)
			.Children.OfType<FilterInstanceEditorControl>().Single();
		ListBox list = InstanceField<ListBox>(instance, "_stages");
		list.SelectedIndex = 1;
		Assert.That(((ListBoxItem)list.SelectedItem!).Tag,
			Is.EqualTo(second.StageId));

		FilterStageDefinition current = definition.GetStage(second.StageId);
		var changedDefaults = current.DefaultParameters.ToDictionary(p => p.Key, p => p.Value);
		changedDefaults["GainDb"] = FilterParameterValue.FromNumber(6);
		FilterDocumentEditor.ApplyStage(work, definition, second.StageId, current,
			"peak", current.Kind, changedDefaults);
		NotifySharedFilterChanged(editor, id);
		Assert.That(((ListBoxItem)list.SelectedItem!).Tag,
			Is.EqualTo(second.StageId));
		Assert.That(InstanceField<Dictionary<string, Control>>(instance, "_editors")
			["GainDb"], Is.TypeOf<TextBox>());
		Assert.That(((TextBox)InstanceField<Dictionary<string, Control>>(
			instance, "_editors")["GainDb"]).Text, Is.EqualTo("6"),
			"same-kind shared default edits must refresh displayed values");

		current = definition.GetStage(second.StageId);
		FilterDocumentEditor.MoveStage(work, definition, second.StageId, 0, current);
		NotifySharedFilterChanged(editor, id);
		Assert.That(((ListBoxItem)list.SelectedItem!).Tag,
			Is.EqualTo(second.StageId));
		Assert.That(((ListBoxItem)list.Items[0]!).Tag, Is.EqualTo(second.StageId));
		Assert.That(StageSummary(Inspector(editor)).Children
			.OfType<TextBlock>().First().Text, Does.StartWith("peak (stage #"),
			"the outer Synth port summary must follow shared stage ordering");

		FilterStageDefinition third = FilterDocumentEditor.AddStage(
			work, definition, FilterKind.SvfNotch);
		NotifySharedFilterChanged(editor, id);
		Assert.That(list.Items.Count, Is.EqualTo(3));
		Assert.That(((ListBoxItem)list.SelectedItem!).Tag,
			Is.EqualTo(second.StageId));
		FilterDocumentEditor.RemoveStage(work, definition, second.StageId,
			definition.GetStage(second.StageId));
		NotifySharedFilterChanged(editor, id);
		Assert.That(list.ItemCount, Is.EqualTo(2));
		Assert.That(((ListBoxItem)list.SelectedItem!).Tag,
			Is.Not.EqualTo(second.StageId));
		Assert.That(list.Items.Cast<ListBoxItem>().Any(x =>
			x.Tag is int stageId && stageId == third.StageId), Is.True);
	}

	[AvaloniaTest]
	public void DirtyOverrideDraftSurvivesSharedKindChangeUntilExplicitReload()
	{
		DocumentWorkspace work = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(work, "Conflict");
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(work, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(work, synth, 100, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			work, synth, id, new FilterInstance(definition));
		FmSynthEditorControl editor = SynthEditor(work, synth, id);
		FilterInstanceEditorControl instance = Inspector(editor).Children
			.OfType<FilterInstanceEditorControl>().Single();
		var toggles = InstanceField<Dictionary<string, CheckBox>>(
			instance, "_overrideToggles");
		var inputs = InstanceField<Dictionary<string, Control>>(instance, "_editors");
		toggles["CutoffHz"].IsChecked = true;
		((TextBox)inputs["CutoffHz"]).Text = "3900";

		FilterStageDefinition previous = definition.GetStage(1);
		FilterDocumentEditor.ApplyStage(work, definition, 1, previous,
			"newkind", FilterKind.SvfNotch,
			new FilterStageDefinition(1, "newkind", FilterKind.SvfNotch)
				.DefaultParameters);
		NotifySharedFilterChanged(editor, id);
		Assert.That(((TextBox)inputs["CutoffHz"]).Text, Is.EqualTo("3900"));
		Assert.That(InstanceField<Dictionary<string, Control>>(
			instance, "_editors")["CutoffHz"], Is.SameAs(inputs["CutoffHz"]));
		Assert.That(InstanceField<TextBlock>(instance, "_message").Text,
			Does.Contain("preserved"));
		Assert.That(((ListBoxItem)InstanceField<ListBox>(
			instance, "_stages").SelectedItem!).Content!.ToString(),
			Does.Contain("newkind — SvfNotch"),
			"stage list shows the actual shared definition while the draft remains");
		var reload = typeof(FilterInstanceEditorControl).GetMethod("Reload",
			BindingFlags.NonPublic | BindingFlags.Instance)!;
		reload.Invoke(instance, null);
		Assert.That(InstanceField<TextBlock>(instance, "_stageLabel").Text,
			Does.Contain("newkind — SvfNotch"));
		Assert.That(InstanceField<Dictionary<string, Control>>(instance, "_editors")
			.Keys, Is.EquivalentTo(FilterCatalogue.GetParameters(FilterKind.SvfNotch)
				.Select(x => x.Name)));
	}

	private static FmSynthEditorControl SynthEditor(
		DocumentWorkspace work, FmSynthDefinition synth, int nodeId)
	{
		FmSynthEditorControl editor = new(work, synth, () => { }, _ => { });
		typeof(FmSynthEditorControl).GetMethod("SelectNode",
			BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, new object[] { nodeId });
		return editor;
	}

	private static StackPanel Inspector(FmSynthEditorControl editor)
		=> (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static T InstanceField<T>(
		FilterInstanceEditorControl editor, string name)
		=> (T)typeof(FilterInstanceEditorControl)
			.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static StackPanel StageSummary(StackPanel inspector)
		=> inspector.Children.OfType<StackPanel>()
			.Single(x => x.Name == "SharedFilterStageSummary");

	private static void NotifySharedFilterChanged(
		FmSynthEditorControl editor, int nodeId)
		=> typeof(FmSynthEditorControl)
			.GetMethod("OnSharedFilterDefinitionCommitted",
				BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, new object[] { nodeId });

	[AvaloniaTest]
	public void BatchPreferenceIsPerFilterNodeAndSurvivesReopeningSynthView()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int one = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 100, 100);
		int two = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 300, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, one, new FilterInstance(filter));
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, two, new FilterInstance(filter));
		FmSynthEditorControl view = SynthEditor(workspace, synth, one);
		FilterInstanceEditorControl current = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		CheckBox toggle = InstanceField<CheckBox>(current, "_batchChanges");
		Assert.That(toggle.IsChecked, Is.False);
		uint audioRevision = workspace.Document.AudioRevision;
		uint documentRevision = workspace.Document.DocumentRevision;
		toggle.IsChecked = true;
		Assert.That(synth.FilterBatchChangesNodeIds.Contains(one), Is.True);
		Assert.That(synth.FilterBatchChangesNodeIds.Contains(two), Is.False);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioRevision),
			"Batch changes is stored editor metadata, not an audio revision");
		Assert.That(workspace.Document.DocumentRevision,
			Is.GreaterThan(documentRevision),
			"changing the persisted preference must mark the song modified");
		SelectFilterNode(view, two);
		current = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		Assert.That(InstanceField<CheckBox>(
			current, "_batchChanges").IsChecked, Is.False);
		SelectFilterNode(view, one);
		current = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		Assert.That(InstanceField<CheckBox>(
			current, "_batchChanges").IsChecked, Is.True);
		view = SynthEditor(workspace, synth, one);
		Assert.That(InstanceField<CheckBox>(
			Inspector(view).Children.OfType<FilterInstanceEditorControl>().Single(),
			"_batchChanges").IsChecked, Is.True,
			"reopening the Synth within this document workspace retains node mode");
		// A saved+reopened song retains the setting because it belongs
		// to its Synth authoring metadata, not a transient workspace dictionary.
		string json = Heresy.Core.Persistence.SongDocumentJson.Serialize(
			workspace.Document, "/tmp/heresy-batch-mode/song.hm.json");
		Heresy.Core.Objects.SongDocument reopened =
			Heresy.Core.Persistence.SongDocumentJson.Deserialize(
				json, "/tmp/heresy-batch-mode/song.hm.json");
		FmSynthDefinition saved = (FmSynthDefinition)reopened.Objects[synth.Id];
		Assert.That(saved.FilterBatchChangesNodeIds.SetEquals([one]), Is.True);
		workspace.New();
		Assert.That(workspace.Document.Objects.ContainsKey(synth.Id), Is.False);
	}

	[AvaloniaTest]
	public void LeavingSynthFilterNodePublishesValidDraftButRejectsInvalidDraft()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int one = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 100, 100);
		int two = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 300, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, one, new FilterInstance(filter));
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, two, new FilterInstance(filter));
		FmSynthEditorControl view = SynthEditor(workspace, synth, one);
		FilterInstanceEditorControl current = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		InstanceField<CheckBox>(current, "_batchChanges").IsChecked = true;
		var flags = InstanceField<Dictionary<string, CheckBox>>(
			current, "_overrideToggles");
		var inputs = InstanceField<Dictionary<string, Control>>(
			current, "_editors");
		flags["CutoffHz"].IsChecked = true;
		((TextBox)inputs["CutoffHz"]).Text = "3700";
		uint before = workspace.Document.AudioRevision;
		SelectFilterNode(view, two);
		Assert.That(workspace.Document.AudioRevision, Is.GreaterThan(before));
		Assert.That(((FmFilterNode)synth.Graph.Nodes.Single(n => n.Id == one))
			.Configuration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(3700),
			"outgoing edits belong to the original Filter node");
		Assert.That(InstanceField<int?>(view, "_selectedNodeId"),
			Is.EqualTo(two));
		SelectFilterNode(view, one);
		current = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		Assert.That(InstanceField<CheckBox>(
			current, "_batchChanges").IsChecked, Is.True);
		inputs = InstanceField<Dictionary<string, Control>>(current, "_editors");
		((TextBox)inputs["CutoffHz"]).Text = "not a cutoff";
		SelectFilterNode(view, two);
		Assert.That(InstanceField<int?>(view, "_selectedNodeId"),
			Is.EqualTo(one), "invalid pending edit must block teardown");
		Assert.That(((TextBox)inputs["CutoffHz"]).Text, Is.EqualTo("not a cutoff"));
		Assert.That(view.TryCommitPendingFilterChanges(), Is.False);
		((TextBox)inputs["CutoffHz"]).Text = "1800";
		Assert.That(view.TryCommitPendingFilterChanges(), Is.True);
		SelectFilterNode(view, two);
		Assert.That(InstanceField<int?>(view, "_selectedNodeId"),
			Is.EqualTo(two));
	}

	[AvaloniaTest]
	public void InvalidFilterDraftBlocksNewNodeCreationBeforeMutatingGraph()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 100, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, id, new FilterInstance(filter));
		FmSynthEditorControl view = SynthEditor(workspace, synth, id);
		FilterInstanceEditorControl instance = Inspector(view).Children
			.OfType<FilterInstanceEditorControl>().Single();
		InstanceField<CheckBox>(instance, "_batchChanges").IsChecked = true;
		var flags = InstanceField<Dictionary<string, CheckBox>>(
			instance, "_overrideToggles");
		var inputs = InstanceField<Dictionary<string, Control>>(
			instance, "_editors");
		flags["CutoffHz"].IsChecked = true;
		((TextBox)inputs["CutoffHz"]).Text = "invalid";
		int count = synth.Graph.Nodes.Count;
		typeof(FmSynthEditorControl).GetMethod("AddConstant",
			BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(view, null);
		Assert.That(synth.Graph.Nodes.Count, Is.EqualTo(count));
		Assert.That(InstanceField<int?>(view, "_selectedNodeId"), Is.EqualTo(id));
		Assert.That(((TextBox)inputs["CutoffHz"]).Text, Is.EqualTo("invalid"));
		((TextBox)inputs["CutoffHz"]).Text = "2500";
		typeof(FmSynthEditorControl).GetMethod("AddConstant",
			BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(view, null);
		Assert.That(synth.Graph.Nodes.Count, Is.EqualTo(count + 1));
		Assert.That(((FmFilterNode)synth.Graph.Nodes.Single(n => n.Id == id))
			.Configuration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(2500));
	}

	[AvaloniaTest]
	public void InvalidPendingOverridePreventsReplacingFilterDefinitionSelection()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int id = FmSynthDocumentEditor.AddFilterNode(workspace, synth, 100, 100);
		FmSynthDocumentEditor.SetFilterConfiguration(
			workspace, synth, id, new FilterInstance(filter));
		FmSynthEditorControl view = SynthEditor(workspace, synth, id);
		StackPanel panel = Inspector(view);
		FilterInstanceEditorControl instance = panel.Children
			.OfType<FilterInstanceEditorControl>().Single();
		InstanceField<CheckBox>(instance, "_batchChanges").IsChecked = true;
		var flags = InstanceField<Dictionary<string, CheckBox>>(
			instance, "_overrideToggles");
		var inputs = InstanceField<Dictionary<string, Control>>(
			instance, "_editors");
		flags["CutoffHz"].IsChecked = true;
		((TextBox)inputs["CutoffHz"]).Text = "invalid";
		int selectorLabel = panel.Children
			.Select((child, index) => (child, index))
			.Single(pair => pair.child is TextBlock text
				&& text.Text == "Shared Filter definition").index;
		StackPanel selectorRow = (StackPanel)panel.Children[selectorLabel + 1];
		ComboBox selection = (ComboBox)selectorRow.Children[0];
		object previous = selection.SelectedItem!;
		uint rev = workspace.Document.AudioRevision;
		selection.SelectedIndex = 1; // (None) would discard the entire configured use.
		Assert.That(selection.SelectedItem, Is.SameAs(previous));
		Assert.That(((FmFilterNode)synth.Graph.Nodes.Single(n => n.Id == id))
			.Configuration, Is.Not.Null);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(rev));
		Assert.That(((TextBox)inputs["CutoffHz"]).Text, Is.EqualTo("invalid"));
	}

	private static void SelectFilterNode(FmSynthEditorControl view, int nodeId)
		=> typeof(FmSynthEditorControl).GetMethod("SelectNode",
			BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(view, new object[] { nodeId });

	private static T InstanceField<T>(FmSynthEditorControl view, string name)
		=> (T)typeof(FmSynthEditorControl)
			.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(view)!;

	[Test]
	public void FilterPortsExpandOnlyForModulatableParameters()
	{
		FilterDefinition definition = new((Heresy.Core.Objects.ObjectId)3U,
			"Filters", new[] {
				new FilterStageDefinition(1, "gain", FilterKind.BiquadPeaking),
				new FilterStageDefinition(2, "delay", FilterKind.CombFeedforward),
			});
		FmFilterNode node = new(7, configuration: new FilterInstance(definition));
		// Biquad: Cutoff, Q, GainDb. Feedforward comb: Feedforward, Mix.
		// Its structural DelaySeconds must not create an FM port.
		Assert.That(node.GetPorts().Select(port => port.ParameterName),
			Is.EqualTo(new[] {
				"Input", "CutoffHz", "Q", "GainDb", "Feedforward", "Mix",
			}));
		Assert.That(Heresy.UserInterface.FmEditing.FmSynthConnectionPorts
			.GetNodeHeight(node), Is.GreaterThan(72));
	}
}
