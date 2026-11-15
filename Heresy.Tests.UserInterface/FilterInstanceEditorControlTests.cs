using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterInstanceEditorControlTests
{
	[AvaloniaTest]
	public void NarrowInspectorUsesTwoColumnSharedGridForOverrideAndValue()
	{
		FilterDefinition definition = new((ObjectId)48U, "Multi-stage", new[] {
			new FilterStageDefinition(1, "low", FilterKind.OnePole),
			new FilterStageDefinition(2, "peak", FilterKind.BiquadPeaking),
		});
		FilterInstance use = new(definition);
		FilterInstanceEditorControl editor = Editor(() => use, x => use = x);
		DockPanel root = (DockPanel)editor.Content!;
		ScrollViewer viewport = root.Children.OfType<ScrollViewer>().Single();
		Assert.That(viewport.HorizontalScrollBarVisibility,
			Is.EqualTo(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled),
			"the embedded inspector must not hide controls horizontally");
		Assert.That(viewport.VerticalScrollBarVisibility,
			Is.EqualTo(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto));

		Grid layout = (Grid)viewport.Content!;
		Grid parameters = Field<Grid>(editor, "_parameters");
		ListBox stages = Field<ListBox>(editor, "_stages");
		TextBlock selectedStage = Field<TextBlock>(editor, "_stageLabel");
		Assert.Multiple(() =>
		{
			Assert.That(parameters, Is.SameAs(layout),
				"all inspector content shares a single two-column grid");
			Assert.That(layout.ColumnDefinitions.Count, Is.EqualTo(2));
			Assert.That(layout.ColumnDefinitions[0].Width.GridUnitType,
				Is.EqualTo(Avalonia.Controls.GridUnitType.Auto));
			Assert.That(layout.ColumnDefinitions[1].Width.GridUnitType,
				Is.EqualTo(Avalonia.Controls.GridUnitType.Star));
			Assert.That(Grid.GetRow(stages), Is.LessThan(Grid.GetRow(selectedStage)));
			Assert.That(Grid.GetColumnSpan(stages), Is.EqualTo(2));
			Assert.That(Grid.GetColumnSpan(selectedStage), Is.EqualTo(2));
		});
		VerifyParameterPairLayout(editor, "CutoffHz");
		Assert.That(layout.Children.OfType<TextBlock>()
			.Any(t => t.Text == "Inherited default: 1000 Hz"
				&& Grid.GetColumnSpan(t) == 2), Is.True);
		stages.SelectedIndex = 1;
		Assert.That(Field<Dictionary<string, Control>>(editor, "_editors").Keys,
			Does.Contain("GainDb"),
			"switching stages must retain its normal inspector behavior");
		VerifyParameterPairLayout(editor, "GainDb");
	}

	private static void VerifyParameterPairLayout(
		FilterInstanceEditorControl editor, string name)
	{
		Grid layout = Field<Grid>(editor, "_parameters");
		CheckBox toggle = Field<Dictionary<string, CheckBox>>(
			editor, "_overrideToggles")[name];
		Control value = Field<Dictionary<string, Control>>(
			editor, "_editors")[name];
		Assert.Multiple(() =>
		{
			Assert.That(layout.Children.Contains(toggle), Is.True);
			Assert.That(layout.Children.Contains(value), Is.True);
			Assert.That(Grid.GetRow(toggle), Is.EqualTo(Grid.GetRow(value)),
				"Override and value occupy the same row");
			Assert.That(Grid.GetColumn(toggle), Is.EqualTo(0));
			Assert.That(Grid.GetColumn(value), Is.EqualTo(1));
			Assert.That(Grid.GetColumnSpan(toggle), Is.EqualTo(1));
			Assert.That(Grid.GetColumnSpan(value), Is.EqualTo(1));
		});
	}

	[AvaloniaTest]
	public void SynthFocusLossAutoAppliesInPlaceAndDoesNotRecreateInputs()
	{
		FilterDefinition definition = new((ObjectId)49U, "Focus");
		FilterInstance current = new(definition);
		int publishes = 0;
		FilterInstanceEditorControl editor = new(
			new FilterInstanceEditSession(() => current, next =>
			{
				current = next;
				publishes++;
			}), _ => { }, synthBatchMode: true);
		CheckBox batch = Field<CheckBox>(editor, "_batchChanges");
		Assert.That(batch.IsChecked, Is.False);
		var toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		var values = Field<Dictionary<string, Control>>(editor, "_editors");
		TextBox cutoff = (TextBox)values["CutoffHz"];
		toggles["CutoffHz"].IsChecked = true;
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.EqualTo(1),
			"leaving Override commits an explicit use even at the shared default");
		Assert.That(values["CutoffHz"], Is.SameAs(cutoff),
			"auto-apply must preserve the next focused input");
		cutoff.Text = "3900";
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.EqualTo(2));
		Assert.That(current.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(3900));
		Assert.That(Field<Dictionary<string, Control>>(
			editor, "_editors")["CutoffHz"], Is.SameAs(cutoff));
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.EqualTo(2), "clean input must not republish");
		cutoff.Text = "not numeric";
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.EqualTo(2));
		Assert.That(((TextBox)values["CutoffHz"]).Text, Is.EqualTo("not numeric"));
		Assert.That(editor.TryCommitPendingChanges(), Is.False,
			"invalid draft blocks safe navigation without discarding input");
		cutoff.Text = "2200";
		Assert.That(editor.TryCommitPendingChanges(), Is.True);
		Assert.That(publishes, Is.EqualTo(3));
		// Explicit teardown commit reconstructs the editor; only subsequent
		// focus-loss commits are required to preserve these new controls.
		cutoff = (TextBox)Field<Dictionary<string, Control>>(
			editor, "_editors")["CutoffHz"];
		toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		toggles["CutoffHz"].IsChecked = false;
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.EqualTo(4));
		Assert.That(current.GetOverrides(1), Is.Empty);
		Assert.That(Field<Dictionary<string, Control>>(editor, "_editors")
			["CutoffHz"], Is.SameAs(cutoff));
		Assert.That(cutoff.Text, Is.EqualTo("1000"),
			"reinheriting must immediately redisplay the shared default");
	}

	[AvaloniaTest]
	public void BatchModeDefersFocusLossButCommitsDirtyChoiceAndNumericAtExit()
	{
		FilterDefinition definition = new((ObjectId)50U, "Choices", [
			new FilterStageDefinition(1, "band", FilterKind.FirBandPass),
		]);
		FilterInstance current = new(definition);
		int publishes = 0;
		bool? lastPreference = null;
		FilterInstanceEditorControl editor = new(
			new FilterInstanceEditSession(() => current, next =>
			{
				current = next;
				publishes++;
			}), _ => { }, synthBatchMode: true,
			initialBatchChanges: true,
			batchChangesChanged: state => lastPreference = state);
		CheckBox batch = Field<CheckBox>(editor, "_batchChanges");
		Assert.That(batch.IsChecked, Is.True);
		var toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		var values = Field<Dictionary<string, Control>>(editor, "_editors");
		foreach (string name in new[] { "LowCutoffHz", "HighCutoffHz", "PhaseResponse" })
			toggles[name].IsChecked = true;
		((TextBox)values["LowCutoffHz"]).Text = "2000";
		((TextBox)values["HighCutoffHz"]).Text = "9500";
		((ComboBox)values["PhaseResponse"]).SelectedItem = "Minimum";
		Invoke(editor, "ApplyOnFocusLoss");
		Assert.That(publishes, Is.Zero, "batch mode must defer focus-loss edits");
		Assert.That(editor.TryCommitPendingChanges(), Is.True);
		Assert.That(publishes, Is.EqualTo(1));
		Assert.That(current.GetStageParameters(1)
			.GetParameter("PhaseResponse").ChoiceValue, Is.EqualTo("Minimum"));
		Assert.That(current.GetStageParameters(1)
			.GetParameter("LowCutoffHz").NumericValue, Is.EqualTo(2000));
		batch.IsChecked = false;
		Assert.That(lastPreference, Is.False);
		Assert.That(publishes, Is.EqualTo(1),
			"changing batch preference alone cannot mutate the song");
		batch.IsChecked = true;
		var refreshed = Field<Dictionary<string, Control>>(editor, "_editors");
		((TextBox)refreshed["LowCutoffHz"]).Text = "2500";
		batch.IsChecked = false;
		Assert.That(publishes, Is.EqualTo(2),
			"exiting Batch changes must commit already-unfocused valid edits");
		Assert.That(current.GetStageParameters(1)
			.GetParameter("LowCutoffHz").NumericValue, Is.EqualTo(2500));
	}

	[AvaloniaTest]
	public void DirtyStageSwitchInSynthBatchModeCommitsBeforeSwitchAndRejectsInvalid()
	{
		FilterDefinition definition = new((ObjectId)51U, "Stages", [
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
			new FilterStageDefinition(2, "second", FilterKind.BiquadPeaking),
		]);
		FilterInstance current = new(definition);
		FilterInstanceEditorControl editor = new(
			new FilterInstanceEditSession(() => current,
				next => current = next), _ => { },
			synthBatchMode: true, initialBatchChanges: true);
		var toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		var values = Field<Dictionary<string, Control>>(editor, "_editors");
		toggles["CutoffHz"].IsChecked = true;
		((TextBox)values["CutoffHz"]).Text = "bad";
		ListBox stages = Field<ListBox>(editor, "_stages");
		stages.SelectedIndex = 1;
		Assert.That(stages.SelectedIndex, Is.Zero);
		Assert.That(((TextBox)values["CutoffHz"]).Text, Is.EqualTo("bad"));
		((TextBox)values["CutoffHz"]).Text = "2800";
		stages.SelectedIndex = 1;
		Assert.That(stages.SelectedIndex, Is.EqualTo(1));
		Assert.That(current.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(2800));
		Assert.That(Field<Dictionary<string, Control>>(
			editor, "_editors").Keys, Does.Contain("GainDb"));
	}

	[AvaloniaTest]
	public void InheritedFieldsAreDisabledAndDisplaySharedDefaultsWithUnits()
	{
		FilterDefinition definition = new((ObjectId)40U, "Pole");
		FilterInstance current = new(definition);
		FilterInstanceEditorControl editor = Editor(() => current, x => current = x);
		Dictionary<string, CheckBox> toggles =
			Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		Dictionary<string, Control> values =
			Field<Dictionary<string, Control>>(editor, "_editors");
		Grid parameters = Field<Grid>(editor, "_parameters");
		Assert.That(toggles.Keys, Is.EquivalentTo(
			FilterCatalogue.GetParameters(FilterKind.OnePole).Select(x => x.Name)));
		Assert.That(toggles.Values.All(x => x.IsChecked != true), Is.True);
		Assert.That(values.Values.All(x => !x.IsEnabled), Is.True);
		Assert.That(((TextBox)values["CutoffHz"]).Text, Is.EqualTo("1000"));
		Assert.That(parameters.Children.OfType<TextBlock>()
			.Any(t => t.Text == "Inherited default: 1000 Hz"
				&& Grid.GetColumnSpan(t) == 2), Is.True);
		Assert.That(ToolTip.GetTip(values["CutoffHz"])?.ToString(),
			Does.Contain("Permitted: 1 to 20000"));
	}

	[AvaloniaTest]
	public void NumericOverrideApplyThenReinheritDoesNotTouchSharedDefinition()
	{
		FilterDefinition definition = new((ObjectId)41U, "Pole");
		FilterInstance current = new(definition);
		int commits = 0;
		FilterInstanceEditorControl editor = Editor(
			() => current, x => { current = x; commits++; });
		Dictionary<string, CheckBox> toggles =
			Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		Dictionary<string, Control> inputs =
			Field<Dictionary<string, Control>>(editor, "_editors");
		TextBox cutoff = (TextBox)inputs["CutoffHz"];
		Assert.That(cutoff.IsEnabled, Is.False);
		toggles["CutoffHz"].IsChecked = true;
		Assert.That(cutoff.IsEnabled, Is.True);
		cutoff.Text = "not a number";
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.Zero);
		Assert.That(current.GetOverrides(1), Is.Empty);
		Assert.That(Field<TextBlock>(editor, "_message").Text,
			Does.Contain("finite"));

		cutoff.Text = "4200";
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.EqualTo(1));
		Assert.That(current.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(4200));
		Assert.That(definition.GetStage(1).DefaultParameters["CutoffHz"]
			.NumericValue, Is.EqualTo(1000));
		Assert.That(current.GetOverrides(1).Count, Is.EqualTo(1));

		toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		toggles["CutoffHz"].IsChecked = false;
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.EqualTo(2));
		Assert.That(current.GetOverrides(1), Is.Empty);
		Assert.That(current.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue, Is.EqualTo(1000));
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.EqualTo(2), "No-op must not publish again.");
	}

	[AvaloniaTest]
	public void StructuralChoiceAndFIRBandEdgesValidateAsOneAtomicBatch()
	{
		FilterDefinition definition = new((ObjectId)42U, "Band", new[] {
			new FilterStageDefinition(9, "stage9", FilterKind.FirBandPass),
		});
		FilterInstance current = new(definition);
		int commits = 0;
		FilterInstanceEditorControl editor = Editor(
			() => current, x => { current = x; commits++; });
		var toggles = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		var inputs = Field<Dictionary<string, Control>>(editor, "_editors");
		Assert.That(inputs["TapCount"], Is.TypeOf<TextBox>());
		Assert.That(ToolTip.GetTip(inputs["TapCount"])?.ToString(),
			Does.Contain("Structural"));

		foreach (string name in new[] { "LowCutoffHz", "HighCutoffHz", "TapCount",
			"PhaseResponse" })
			toggles[name].IsChecked = true;
		((TextBox)inputs["LowCutoffHz"]).Text = "9000";
		((TextBox)inputs["HighCutoffHz"]).Text = "3000";
		((TextBox)inputs["TapCount"]).Text = "255";
		((ComboBox)inputs["PhaseResponse"]).SelectedItem = "Minimum";
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.Zero);
		Assert.That(current.GetOverrides(9), Is.Empty);

		((TextBox)inputs["HighCutoffHz"]).Text = "12000";
		Invoke(editor, "ApplyDraft");
		Assert.That(commits, Is.EqualTo(1));
		Assert.That(current.GetOverrides(9).Count, Is.EqualTo(4));
		Assert.That(current.GetStageParameters(9).GetParameter("PhaseResponse")
			.ChoiceValue, Is.EqualTo("Minimum"));
		Assert.That(current.GetStageParameters(9).GetParameter("TapCount")
			.NumericValue, Is.EqualTo(255));
		Assert.That(definition.GetStage(9).DefaultParameters["PhaseResponse"]
			.ChoiceValue, Is.EqualTo("Linear"));
	}

	[AvaloniaTest]
	public void ExternalEditDoesNotDiscardUnsavedDraftOrCommitItOverNewVersion()
	{
		FilterDefinition definition = new((ObjectId)43U, "Shared");
		FilterInstance current = new(definition);
		FilterInstanceEditorControl one = Editor(() => current, x => current = x);
		FilterInstanceEditorControl two = Editor(() => current, x => current = x);
		var oneToggles = Field<Dictionary<string, CheckBox>>(one, "_overrideToggles");
		var oneValues = Field<Dictionary<string, Control>>(one, "_editors");
		oneToggles["CutoffHz"].IsChecked = true;
		((TextBox)oneValues["CutoffHz"]).Text = "3900";

		var twoToggles = Field<Dictionary<string, CheckBox>>(two, "_overrideToggles");
		var twoValues = Field<Dictionary<string, Control>>(two, "_editors");
		twoToggles["CutoffHz"].IsChecked = true;
		((TextBox)twoValues["CutoffHz"]).Text = "1800";
		Invoke(two, "ApplyDraft");

		one.NotifyConfigurationChanged();
		Assert.That(((TextBox)oneValues["CutoffHz"]).Text, Is.EqualTo("3900"));
		Assert.That(Field<TextBlock>(one, "_message").Text,
			Does.Contain("preserved"));
		Invoke(one, "ApplyDraft");
		Assert.That(current.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue, Is.EqualTo(1800));
		Assert.That(((TextBox)oneValues["CutoffHz"]).Text, Is.EqualTo("3900"));
		Invoke(one, "Reload");
		Assert.That(((TextBox)Field<Dictionary<string, Control>>(
			one, "_editors")["CutoffHz"]).Text, Is.EqualTo("1800"));
	}

	[AvaloniaTest]
	public void StageSwitchBlocksDirtyDraftThenReloadAllowsOtherStage()
	{
		FilterDefinition definition = new((ObjectId)44U, "Multi", new[] {
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
			new FilterStageDefinition(2, "second", FilterKind.BiquadPeaking),
		});
		FilterInstance use = new(definition);
		FilterInstanceEditorControl editor = Editor(() => use, x => use = x);
		var checks = Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		var input = Field<Dictionary<string, Control>>(editor, "_editors");
		checks["CutoffHz"].IsChecked = true;
		((TextBox)input["CutoffHz"]).Text = "2200";
		ListBox stages = Field<ListBox>(editor, "_stages");
		stages.SelectedIndex = 1;
		Assert.That(stages.SelectedIndex, Is.EqualTo(0));
		Assert.That(((TextBox)input["CutoffHz"]).Text, Is.EqualTo("2200"));
		Invoke(editor, "Reload");
		stages.SelectedIndex = 1;
		Assert.That(Field<TextBlock>(editor, "_stageLabel").Text,
			Does.Contain("second"));
		Assert.That(Field<Dictionary<string, Control>>(editor, "_editors")
			.Keys, Does.Contain("GainDb"));
	}

	private static FilterInstanceEditorControl Editor(
		Func<FilterInstance> current, Action<FilterInstance> publish)
		=> new(new FilterInstanceEditSession(current, publish), _ => { });

	private static T Field<T>(FilterInstanceEditorControl editor, string name)
		=> (T)typeof(FilterInstanceEditorControl)
			.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static void Invoke(FilterInstanceEditorControl editor, string name)
		=> typeof(FilterInstanceEditorControl)
			.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, null);
}
