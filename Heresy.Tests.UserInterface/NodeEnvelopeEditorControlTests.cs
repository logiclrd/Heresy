using System;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeEditorControlTests
{
	[AvaloniaTest]
	public void StandaloneAndFmEditorsShareNodeEditingWithAtomicApply()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "LFO");
		string? notification = null;
		NodeEnvelopeEditorControl editor = new(
			workspace, envelope, msg => notification = msg);
		uint initial = workspace.Document.AudioRevision;

		TextBox nodes = Field<TextBox>(editor, "_nodesInput");
		TextBox start = Field<TextBox>(editor, "_sustainStart");
		TextBox end = Field<TextBox>(editor, "_sustainEnd");
		ComboBox mode = Field<ComboBox>(editor, "_loopMode");

		nodes.Text = "0, -0.5\n0.25, 1.5\n0.5, 0.25\n0.8, 0";
		start.Text = "1";
		end.Text = "2";
		mode.SelectedItem = EnvelopeSustainLoopMode.PingPong;
		Invoke(editor, "Apply");

		workspace.Document.AudioRevision.Should().Be(initial + 1);
		envelope.Nodes.Should().HaveCount(4);
		envelope.Nodes[1].Time.Should().Be(TimeSpan.FromMilliseconds(250));
		envelope.Nodes[1].Value.Should().Be(1.5);
		envelope.Nodes[0].Value.Should().Be(-0.5);
		envelope.SustainStartIndex.Should().Be(1);
		envelope.SustainEndIndex.Should().Be(2);
		envelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.PingPong);
		notification.Should().Contain("Updated envelope");

		Invoke(editor, "Apply");
		workspace.Document.AudioRevision.Should().Be(initial + 1);
		nodes.Text = "0, 0\n0, 1";
		start.Text = "0";
		end.Text = "1";
		Invoke(editor, "Apply");
		workspace.Document.AudioRevision.Should().Be(initial + 1);
		envelope.Nodes[1].Value.Should().Be(1.5);
		Field<TextBlock>(editor, "_message").Text.Should()
			.Contain("increase strictly");
	}

	[AvaloniaTest]
	public void InvalidNodeTextNeverMutatesCurveOrDocument()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Curve");
		NodeEnvelopeEditorControl editor = new(workspace, envelope, _ => { });
		uint rev = workspace.Document.AudioRevision;
		Field<TextBox>(editor, "_nodesInput").Text = "not a time";
		Invoke(editor, "Apply");
		workspace.Document.AudioRevision.Should().Be(rev);
		envelope.Nodes.Should().HaveCount(4);
	}

	private static T Field<T>(NodeEnvelopeEditorControl editor, string name)
		=> (T)typeof(NodeEnvelopeEditorControl).GetField(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;

	private static void Invoke(NodeEnvelopeEditorControl editor, string name)
		=> typeof(NodeEnvelopeEditorControl).GetMethod(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(editor, []);
}
