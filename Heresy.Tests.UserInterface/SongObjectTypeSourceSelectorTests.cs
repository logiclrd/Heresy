using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Styling;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectTypeSourceSelectorTests
{
	private const BindingFlags PrivateInstance =
		BindingFlags.Instance | BindingFlags.NonPublic;

	private static Color GetColour(Control content)
	{
		Border badge = (Border)content;
		return ((SolidColorBrush)badge.Background!).Color;
	}

	private static Control RenderChoice(ComboBox box, object option)
		=> (Control)box.ItemTemplate!.Build(option)!;

	[AvaloniaTest]
	public void InstrumentSourceTemplatesColourBothPopupAndSelectedValueWithoutRowBackground()
	{
		DocumentWorkspace workspace = new();
		ObjectId sampleId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new SampleDefinition(sampleId, "Kick",
			new ExternalAssetReference(Path.GetFullPath("sample.wav"))));
		ObjectId scriptedId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptInstrumentDefinition(scriptedId, "Script Bass"));
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		InstrumentEditorControl editor = new(
			workspace, instrument, () => { }, _ => { });
		ComboBox source = (ComboBox)typeof(InstrumentEditorControl)
			.GetMethod("BuildSourceBox", PrivateInstance)!
			.Invoke(editor, new object?[] { sampleId })!;
		Assert.That(source.SelectedItem, Is.Not.Null);
		Assert.That(source.ItemTemplate, Is.Not.Null);
		Assert.That(GetColour(RenderChoice(source, source.SelectedItem!)),
			Is.EqualTo(SongObjectTypePalette.Sample),
			"closed-cell presentation uses the Source ItemTemplate");
		object scriptChoice = ((IEnumerable)source.ItemsSource!)
			.Cast<object>().Single(x => x.ToString()!.Contains("Script Bass"));
		Assert.That(GetColour(RenderChoice(source, scriptChoice)),
			Is.EqualTo(SongObjectTypePalette.ScriptedInstrument),
			"the same template also supplies expanded dropdown entries");
		source.SelectedItem = scriptChoice;
		Assert.That(GetColour(RenderChoice(source, source.SelectedItem!)),
			Is.EqualTo(SongObjectTypePalette.ScriptedInstrument));
		Assert.That(source.Background, Is.Null.Or.Not.EqualTo(
			new SolidColorBrush(SongObjectTypePalette.ScriptedInstrument)),
			"only the name badge is coloured, not the entire editor cell");
		object missing = ((IEnumerable)source.ItemsSource!).Cast<object>()
			.First(x => x.ToString() == "");
		Assert.That(RenderChoice(source, missing), Is.TypeOf<TextBlock>(),
			"unassigned Source must remain uncoloured");
	}

	[AvaloniaTest]
	public void SynthSourceDropdownAndGraphStatusUseTheSameLiveObjectColourAndName()
	{
		DocumentWorkspace workspace = new();
		ObjectId sampleId = workspace.Document.AllocateObjectId();
		SampleDefinition sample = new(sampleId, "Kick",
			new ExternalAssetReference(Path.GetFullPath("sample.wav")));
		workspace.Document.Add(sample);
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
			workspace, "Player");
		int nodeId = FmSynthDocumentEditor.AddSoundSourceNode(
			workspace, synth, 240, 120);
		FmSynthDocumentEditor.UpdateNode(workspace, synth,
			new FmSoundSourceNode(nodeId, sampleId, 261.62));
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, nodeId);

		FmSynthEditorControl editor = new(
			workspace, synth, () => { }, _ => { });
		StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", PrivateInstance)!.GetValue(editor)!;
		ComboBox selector = inspector.Children.OfType<ComboBox>()
			.First(c => c.Items.OfType<object>().Any(x =>
				x.ToString()!.Contains("Kick")));
		Assert.That(GetColour(RenderChoice(selector, selector.SelectedItem!)),
			Is.EqualTo(SongObjectTypePalette.Sample));
		Assert.That(((TextBlock)((Border)RenderChoice(
			selector, selector.SelectedItem!)).Child!).Text,
			Does.Contain("Kick"));

		FmSynthGraphCanvas canvas = new(
			synth, _ => { }, (_, _, _) => { }, (_, _, _) => { },
			(_, _, _, _) => { },
			document: workspace.Document);
		var nodes = (Dictionary<int, Border>)typeof(FmSynthGraphCanvas)
			.GetField("_nodeControls", PrivateInstance)!.GetValue(canvas)!;
		Border node = nodes[nodeId];
		Grid root = (Grid)node.Child!;
		StackPanel layout = root.Children.OfType<StackPanel>().Single();
		Border statusRow = layout.Children.OfType<Border>()
			.Single(x => x.Name == "SingleInputStatusRow");
		Border badge = (Border)statusRow.Child!;
		Assert.That(GetColour(badge), Is.EqualTo(SongObjectTypePalette.Sample));
		Assert.That(((TextBlock)badge.Child!).Text, Is.EqualTo("Kick"));

		sample.Name = "Renamed Kick";
		canvas.Refresh();
		nodes = (Dictionary<int, Border>)typeof(FmSynthGraphCanvas)
			.GetField("_nodeControls", PrivateInstance)!.GetValue(canvas)!;
		root = (Grid)nodes[nodeId].Child!;
		layout = root.Children.OfType<StackPanel>().Single();
		statusRow = layout.Children.OfType<Border>()
			.Single(x => x.Name == "SingleInputStatusRow");
		Assert.That(((TextBlock)((Border)statusRow.Child!).Child!).Text,
			Is.EqualTo("Renamed Kick"),
			"node label resolves the referenced object at each canvas refresh");
	}
}
