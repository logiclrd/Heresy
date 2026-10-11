using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
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

	[AvaloniaTest]
	public void TwoMountedEditorsReflectSharedChangesIncludingNumericAndMatchState()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shared");
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM shared");
		int fmNodeId = FmSynthDocumentEditor.AddEnvelopeNode(
			workspace, synth, envelope.Id, 200, 100);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, fmNodeId);

		NodeEnvelopeEditorControl standalone =
			new(workspace, envelope, _ => { });
		FmSynthEditorControl fm =
			new(workspace, synth, () => { }, _ => { });
		StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(fm)!;
		NodeEnvelopeEditorControl sharedInspector =
			inspector.Children.OfType<NodeEnvelopeEditorControl>().Single();
		Window host = new Window
		{
			Width = 1100, Height = 850,
			Content = new StackPanel
			{
				Children = { standalone, fm },
			},
		};
		host.Show();
		try
		{
			TextBox standaloneNodes = Field<TextBox>(standalone, "_nodesInput");
			TextBox fmNodes = Field<TextBox>(sharedInspector, "_nodesInput");
			EnvelopeNode[] modified = envelope.Nodes.ToArray();
			modified[2] = modified[2] with { Value = 0.37 };
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				workspace, envelope, modified, 1, 2,
				EnvelopeSustainLoopMode.PingPong);
			standaloneNodes.Text.Should().Contain("0.37");
			fmNodes.Text.Should().Contain("0.37");
			Field<TextBox>(sharedInspector, "_sustainStart")
				.Text.Should().Be("1");
			Field<TextBox>(sharedInspector, "_sustainEnd")
				.Text.Should().Be("2");
			Field<ComboBox>(sharedInspector, "_loopMode")
				.SelectedItem.Should().Be(EnvelopeSustainLoopMode.PingPong);

			// The two editors really reference the same object. An edit
			// originating in either one must appear in the other.
			fmNodes.Text = "0, 0\n0.01, 0.8\n0.11, 0.2\n0.41, 0";
			Invoke(sharedInspector, "Apply");
			standaloneNodes.Text.Should().Contain("0.8");
			envelope.Nodes[1].Value.Should().Be(0.8);
		}
		finally
		{
			host.Close();
		}
	}

	[AvaloniaTest]
	public void DirtyNumericDraftSurvivesExternalEditAndCannotClobberIt()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Conflict");
		NodeEnvelopeEditorControl first = new(workspace, envelope, _ => { });
		NodeEnvelopeEditorControl second = new(workspace, envelope, _ => { });
		Window host = new Window
		{
			Width = 900, Height = 600,
			Content = new StackPanel { Children = { first, second } },
		};
		host.Show();
		try
		{
			TextBox draft = Field<TextBox>(first, "_nodesInput");
			draft.Text = "0, 0\n0.01, 1.25\n0.11, 0.7\n0.41, 0";
			uint revision = workspace.Document.AudioRevision;
			TextBox current = Field<TextBox>(second, "_nodesInput");
			current.Text = "0, 0\n0.01, 0.25\n0.11, 0.7\n0.41, 0";
			Invoke(second, "Apply");
			workspace.Document.AudioRevision.Should().Be(revision + 1);
			draft.Text.Should().Contain("1.25",
				"an uncommitted numeric edit should not be silently discarded");

			Invoke(first, "Apply");
			envelope.Nodes[1].Value.Should().Be(0.25);
			workspace.Document.AudioRevision.Should().Be(revision + 1);
			Field<TextBlock>(first, "_message").Text.Should()
				.Contain("Reload");

			typeof(NodeEnvelopeEditorControl)
				.GetMethod("RefreshFromEnvelope")!.Invoke(first, []);
			draft.Text.Should().Contain("0.25");
			draft.Text.Should().NotContain("1.25");
		}
		finally
		{
			host.Close();
		}
	}

	[AvaloniaTest]
	public void StandaloneEditorBackButtonReturnsToDocumentNavigation()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Navigate");
		int backCount = 0;
		EnvelopeEditorControl editor = new(
			workspace, envelope, () => backCount++, _ => { });
		Window host = new() { Width = 800, Height = 700, Content = editor };
		host.Show();
		try
		{
			DockPanel root = (DockPanel)editor.Content!;
			DockPanel header = (DockPanel)root.Children[0];
			Button back = (Button)header.Children[0];
			back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			backCount.Should().Be(1);
			workspace.Document.Objects[envelope.Id].Should().BeSameAs(envelope);
		}
		finally { host.Close(); }
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
