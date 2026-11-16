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
	public void CurvatureAndWaveformShareLeftCellWithReactiveRightHandPreview()
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
		Grid row = inspector.Children.OfType<Grid>().Single();
		row.ColumnDefinitions.Should().HaveCount(2);
		StackPanel left = WaveformControls(inspector);
		Grid.GetColumn(left).Should().Be(0);
		left.Children[0].Should().BeOfType<TextBlock>()
			.Which.Text.Should().Be("Waveform");
		ComboBox waveform = left.Children[1]
			.Should().BeOfType<ComboBox>().Which;
		left.Children[2].Should().BeOfType<TextBlock>()
			.Which.Text.Should().Be("Curvature");
		TextBox curvature = CurvatureInput(inspector);
		curvature.Text.Should().Be("0");
		FmSynthWaveformPreview preview = WaveformPreview(inspector);
		preview.Curvature.Should().Be(0.0);
		preview.Waveform.Should().Be(FmOscillatorWaveform.Sawtooth);
		preview.ValueAtFraction(0.0).Should().Be(0.0);
		uint startRevision = workspace.Document.AudioRevision;

		curvature.Text = "2.25";
		Press(curvature, Key.Enter);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		Oscillator(synth, secondId).Curvature.Should().Be(0.0);
		preview.Curvature.Should().Be(2.25);
		preview.ValueAtFraction(0.0).Should().BeLessThan(0.0);
		workspace.Document.AudioRevision.Should().Be(startRevision + 1);

		// Repeating the commit is a no-op; changing waveform keeps curvature.
		Press(curvature, Key.Enter);
		workspace.Document.AudioRevision.Should().Be(startRevision + 1);
		waveform.SelectedItem = FmOscillatorWaveform.Triangle;
		Oscillator(synth, firstId).Waveform.Should().Be(
			FmOscillatorWaveform.Triangle);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		preview.Waveform.Should().Be(FmOscillatorWaveform.Triangle);
		workspace.Document.AudioRevision.Should().Be(startRevision + 2);

		// Rejected edits do not misrepresent the committed waveform.
		curvature.Text = "33";
		Press(curvature, Key.Enter);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);
		preview.Curvature.Should().Be(2.25);
		workspace.Document.AudioRevision.Should().Be(startRevision + 2);
		Press(curvature, Key.Escape);
		curvature.Text.Should().Be("2.25");

		Select(editor, secondId);
		inspector = Inspector(editor);
		TextBox otherCurvature = CurvatureInput(inspector);
		otherCurvature.Text.Should().Be("0");
		WaveformPreview(inspector).Curvature.Should().Be(0.0);
		otherCurvature.Text = "-1.5";
		Press(otherCurvature, Key.Enter);
		Oscillator(synth, secondId).Curvature.Should().Be(-1.5);
		WaveformPreview(inspector).Curvature.Should().Be(-1.5);
		Oscillator(synth, firstId).Curvature.Should().Be(2.25);

		Select(editor, firstId);
		CurvatureInput(Inspector(editor)).Text.Should().Be("2.25");
		WaveformPreview(Inspector(editor)).Curvature.Should().Be(2.25);
	}

	private static StackPanel Inspector(FmSynthEditorControl editor)
		=> (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance
				| BindingFlags.NonPublic)!
			.GetValue(editor)!;

	private static StackPanel WaveformControls(StackPanel inspector)
		=> inspector.Children.OfType<Grid>().Single()
			.Children.OfType<StackPanel>().Single();

	private static TextBox CurvatureInput(StackPanel inspector)
		=> WaveformControls(inspector).Children[3]
			.Should().BeOfType<TextBox>().Which;

	private static FmSynthWaveformPreview WaveformPreview(
		StackPanel inspector)
	{
		Grid row = inspector.Children.OfType<Grid>().Single();
		Border border = row.Children.OfType<Border>().Single();
		Grid.GetColumn(border).Should().Be(1);
		return border.Child.Should()
			.BeOfType<FmSynthWaveformPreview>().Which;
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
