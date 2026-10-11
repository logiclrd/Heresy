using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Filters;
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
	}

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
