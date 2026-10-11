using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Filters;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterEditorControlTests
{
	[AvaloniaTest]
	public void GroupHeadingsAreDisabledAndAllFilterKindsAreSelectable()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(workspace, "Sweep");
		FilterEditorControl editor = NewEditor(workspace, definition);
		ComboBox selector = Field<ComboBox>(editor, "_kind");

		ComboBoxItem[] options = selector.Items.OfType<ComboBoxItem>().ToArray();
		Assert.That(options.Length, Is.EqualTo(FilterCatalogue.MenuItems.Count));
		Assert.That(options.Count(x => !x.IsEnabled), Is.EqualTo(4));
		Assert.That(options.Where(x => x.IsEnabled)
			.Select(x => (FilterKind)x.Tag!),
			Is.EquivalentTo(Enum.GetValues<FilterKind>()));
		Assert.That(options.Where(x => !x.IsEnabled).All(x => x.Tag is null),
			Is.True);
		Assert.That(options.First(x => x.Tag is FilterKind kind
			&& kind == FilterKind.CombFeedforward).IsEnabled, Is.True);
		Assert.That(selector.SelectedItem, Is.Not.Null);
	}

	[AvaloniaTest]
	public void InvalidNumericDraftDoesNotCommitThenValidDraftCommitsOnce()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(workspace, "Sweep");
		FilterEditorControl editor = NewEditor(workspace, definition);
		Dictionary<string, Control> controls =
			Field<Dictionary<string, Control>>(editor, "_editors");
		TextBox cutoff = (TextBox)controls["CutoffHz"];
		TextBox name = Field<TextBox>(editor, "_name");
		TextBlock message = Field<TextBlock>(editor, "_message");
		uint revision = workspace.Document.AudioRevision;

		cutoff.Text = "NaN";
		name.Text = "low";
		Call(editor, "ApplyDraft");
		Assert.That(message.Text, Does.Contain("finite"));
		Assert.That(definition.GetStage(1).Name, Is.EqualTo("stage1"));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));

		cutoff.Text = "2400";
		Call(editor, "ApplyDraft");
		Assert.That(definition.GetStage(1).Name, Is.EqualTo("low"));
		Assert.That(definition.GetStage(1).DefaultParameters["CutoffHz"]
			.NumericValue, Is.EqualTo(2400));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

		Call(editor, "ApplyDraft");
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
	}

	[AvaloniaTest]
	public void ChangingKindBuildsNewTypedFieldsAndValidatesAtomically()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(workspace, "Phase");
		FilterEditorControl editor = NewEditor(workspace, definition);
		ComboBox selector = Field<ComboBox>(editor, "_kind");
		ComboBoxItem choice = selector.Items.OfType<ComboBoxItem>()
			.Single(item => item.Tag is FilterKind kind
				&& kind == FilterKind.FirBandPass);
		selector.SelectedItem = choice;

		Dictionary<string, Control> fields =
			Field<Dictionary<string, Control>>(editor, "_editors");
		Assert.That(fields.Keys, Does.Contain("TapCount"));
		Assert.That(fields.Keys, Does.Contain("PhaseResponse"));
		Assert.That(fields["TapCount"], Is.TypeOf<TextBox>());
		ComboBox phase = (ComboBox)fields["PhaseResponse"];
		Assert.That(phase.ItemsSource!.Cast<string>(),
			Is.EquivalentTo(new[] { "Linear", "Minimum", "Maximum" }));
		phase.SelectedItem = "Minimum";
		((TextBox)fields["TapCount"]).Text = "255";
		((TextBox)fields["LowCutoffHz"]).Text = "6000";
		((TextBox)fields["HighCutoffHz"]).Text = "1000";
		uint audio = workspace.Document.AudioRevision;
		Call(editor, "ApplyDraft");
		Assert.That(definition.GetStage(1).Kind, Is.EqualTo(FilterKind.OnePole));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));

		((TextBox)fields["HighCutoffHz"]).Text = "9000";
		Call(editor, "ApplyDraft");
		Assert.That(definition.GetStage(1).Kind, Is.EqualTo(FilterKind.FirBandPass));
		Assert.That(definition.GetStage(1).DefaultParameters["PhaseResponse"]
			.ChoiceValue, Is.EqualTo("Minimum"));
		Assert.That(definition.GetStage(1).DefaultParameters["TapCount"]
			.NumericValue, Is.EqualTo(255));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
	}

	[AvaloniaTest]
	public void StaleExternalEditDoesNotOverwriteLocallyDirtyFields()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition definition = FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FilterEditorControl editor = NewEditor(workspace, definition);
		TextBox name = Field<TextBox>(editor, "_name");
		name.Text = "local_draft";
		FilterStageDefinition otherEditorBaseline = definition.GetStage(1);
		FilterDocumentEditor.ApplyStage(workspace, definition, 1,
			otherEditorBaseline, "remote", FilterKind.OnePole,
			otherEditorBaseline.DefaultParameters);
		uint revision = workspace.Document.AudioRevision;
		Call(editor, "ApplyDraft");
		Assert.That(definition.GetStage(1).Name, Is.EqualTo("remote"));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
		Assert.That(name.Text, Is.EqualTo("local_draft"));
		Assert.That(Field<TextBlock>(editor, "_message").Text,
			Does.Contain("Reload"));

		Call(editor, "ReloadStage");
		Assert.That(name.Text, Is.EqualTo("remote"));
	}

	private static FilterEditorControl NewEditor(
		DocumentWorkspace workspace, FilterDefinition definition)
		=> new(workspace, definition, () => { }, _ => { });

	private static T Field<T>(FilterEditorControl editor, string name)
		=> (T)typeof(FilterEditorControl)
			.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static void Call(FilterEditorControl editor, string method)
		=> typeof(FilterEditorControl).GetMethod(
			method, BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, null);
}
