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
	public void InheritedFieldsAreDisabledAndDisplaySharedDefaultsWithUnits()
	{
		FilterDefinition definition = new((ObjectId)40U, "Pole");
		FilterInstance current = new(definition);
		FilterInstanceEditorControl editor = Editor(() => current, x => current = x);
		Dictionary<string, CheckBox> toggles =
			Field<Dictionary<string, CheckBox>>(editor, "_overrideToggles");
		Dictionary<string, Control> values =
			Field<Dictionary<string, Control>>(editor, "_editors");
		StackPanel parameters = Field<StackPanel>(editor, "_parameters");
		Assert.That(toggles.Keys, Is.EquivalentTo(
			FilterCatalogue.GetParameters(FilterKind.OnePole).Select(x => x.Name)));
		Assert.That(toggles.Values.All(x => x.IsChecked != true), Is.True);
		Assert.That(values.Values.All(x => !x.IsEnabled), Is.True);
		Assert.That(((TextBox)values["CutoffHz"]).Text, Is.EqualTo("1000"));
		Assert.That(parameters.Children.OfType<StackPanel>()
			.Any(row => row.Children.OfType<TextBlock>()
				.Any(t => t.Text == "Inherited default: 1000 Hz")), Is.True);
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
