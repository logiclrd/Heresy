using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConstantOscillatorInspectorTests
{
	[AvaloniaTest]
	public void ConstantCheckboxFollowsFrequencyInputAndCommitsOnlyChanges()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
			workspace, "Vibrato");
		int oscillatorId = FmSynthDocumentEditor.AddOscillatorNode(
			workspace, synth, FmOscillatorWaveform.Sine, 5.0,
			x: 220.0, y: 80.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, oscillatorId);

		FmSynthEditorControl editor = new(
			workspace, synth, () => { }, _ => { });
		StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;

		int frequencyLabelIndex = inspector.Children
			.Select((child, index) => (child, index))
			.Single(pair => pair.child is TextBlock label
				&& label.Text == "Frequency Hz").index;
		inspector.Children[frequencyLabelIndex + 1].Should()
			.BeOfType<TextBox>().Which.Text.Should().Be("5");
		CheckBox constant = inspector.Children[frequencyLabelIndex + 2]
			.Should().BeOfType<CheckBox>().Which;
		constant.Content.Should().Be("Constant");
		constant.IsChecked.Should().BeFalse();

		uint initialAudio = workspace.Document.AudioRevision;
		uint initialDocument = workspace.Document.DocumentRevision;
		constant.IsChecked = true;

		FmOscillatorNode live = synth.Graph.Nodes
			.OfType<FmOscillatorNode>().Single();
		live.Constant.Should().BeTrue();
		live.FrequencyHz.Should().Be(5.0);
		workspace.Document.AudioRevision.Should().Be(initialAudio + 1);
		workspace.Document.DocumentRevision.Should().Be(initialDocument + 1);

		constant.IsChecked = true;
		workspace.Document.AudioRevision.Should().Be(initialAudio + 1);
		constant.IsChecked = false;
		synth.Graph.Nodes.OfType<FmOscillatorNode>()
			.Single().Constant.Should().BeFalse();
		workspace.Document.AudioRevision.Should().Be(initialAudio + 2);
	}
}
