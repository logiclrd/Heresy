using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthCurvatureInspectorTests
{
	[AvaloniaTest]
	public void CurvatureIsDirectlyBelowWaveformAndEditsOnlyTheSelectedOscillator()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Curved");
		int firstId = FmSynthDocumentEditor.AddOscillatorNode(
			workspace, synth, FmOscillatorWaveform.Sawtooth, 440.0,
			x: 200, y: 80);
		int secondId = FmSynthDocumentEditor.AddOscillatorNode(
			workspace, synth, FmOscillatorWaveform.Sine, 330.0,
			x: 400, y: 80);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, firstId);
		FmSynthEditorControl editor =
			new(workspace, synth, () => { }, _ => { });

		StackPanel inspector = Inspector(editor);
		int waveformIndex = inspector.Children
			.Select((child, index) => (child, index))
			.Single(x => x.child is TextBlock label
				&& label.Text == "Waveform").index;
		ComboBox waveform = inspector.Children[waveformIndex + 1]
			.Should().BeOfType<ComboBox>().Which;
		inspector.Children[waveformIndex + 2]
			.Should().BeOfType<TextBlock>().Which.Text.Should().Be("Curvature");
		TextBox curvature = inspector.Children[waveformIndex + 3]
			.Should().BeOfType<TextBox>().Which;
		curvature.Text.Should().Be("0");
		uint startRevision = workspace.Document.AudioRevision;

		curvature.Text = "2.25";
		Press(curvature, Key.Enter);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		Oscillator(synth, secondId).Curvature.Should().Be(0.0);
		workspace.Document.AudioRevision.Should().Be(startRevision + 1);

		// A repeated commit is a no-op; other fields must preserve curvature.
		Press(curvature, Key.Enter);
		workspace.Document.AudioRevision.Should().Be(startRevision + 1);
		waveform.SelectedItem = FmOscillatorWaveform.Triangle;
		Oscillator(synth, firstId).Waveform.Should().Be(
			FmOscillatorWaveform.Triangle);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		workspace.Document.AudioRevision.Should().Be(startRevision + 2);

		// Invalid values leave the committed graph and Escape baseline intact.
		curvature.Text = "33";
		Press(curvature, Key.Enter);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		workspace.Document.AudioRevision.Should().Be(startRevision + 2);
		Press(curvature, Key.Escape);
		curvature.Text.Should().Be("2.25");

		Select(editor, secondId);
		inspector = Inspector(editor);
		TextBox otherCurvature = CurvatureInput(inspector);
		otherCurvature.Text.Should().Be("0");
		otherCurvature.Text = "-1.5";
		Press(otherCurvature, Key.Enter);
		Oscillator(synth, secondId).Curvature.Should().Be(-1.5);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);

		Select(editor, firstId);
		CurvatureInput(Inspector(editor)).Text.Should().Be("2.25");
	}

	private static StackPanel Inspector(FmSynthEditorControl editor)
		=> (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance
				| BindingFlags.NonPublic)!
			.GetValue(editor)!;

	private static TextBox CurvatureInput(StackPanel inspector)
	{
		int index = inspector.Children
			.Select((child, i) => (child, i))
			.Single(x => x.child is TextBlock label
				&& label.Text == "Curvature").i;
		return inspector.Children[index + 1]
			.Should().BeOfType<TextBox>().Which;
	}

	private static FmOscillatorNode Oscillator(FmSynthDefinition synth, int id)
		=> synth.Graph.Nodes.OfType<FmOscillatorNode>()
			.Single(node => node.Id == id);

	private static void Select(FmSynthEditorControl editor, int id)
		=> typeof(FmSynthEditorControl)
			.GetMethod("SelectNode",
				BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(editor, [id]);

	private static void Press(Control target, Key key)
		=> target.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = key,
		});
}
