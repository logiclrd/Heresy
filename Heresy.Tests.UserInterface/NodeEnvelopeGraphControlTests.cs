using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.EnvelopeEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeGraphControlTests
{
	[AvaloniaTest]
	public void GraphCommitUpdatesSharedEnvelopeAndSynchronizesNodeList()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shared");
		string? changed = null;
		NodeEnvelopeEditorControl editor = new(
			workspace, envelope, s => changed = s);
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph",
					BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(editor)!;
		uint revision = workspace.Document.AudioRevision;
		EnvelopeNode[] nodes = envelope.Nodes.ToArray();
		nodes[1] = new EnvelopeNode(
			TimeSpan.FromMilliseconds(20), 2.5);
		NodeEnvelopeGraphEdit request =
			new(nodes, 2, 2, SelectedNodeIndex: 1);
		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [request]);
		envelope.Nodes[1].Value.Should().Be(2.5);
		envelope.Nodes[1].Time.Should().Be(TimeSpan.FromMilliseconds(20));
		graph.SelectedNodeIndex.Should().Be(1);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		changed.Should().Contain("Updated envelope");
		TextBox text = (TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_nodesInput",
				BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;
		text.Text.Should().Contain("0.02, 2.5");

		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [request]);
		workspace.Document.AudioRevision.Should().Be(revision + 1,
			"equivalent edits do not generate extra audio revisions");
	}

	[AvaloniaTest]
	public void MatchAppearsForSelectedDistinctEndpointAndCommitsOneValue()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Match test");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.PingPong);
		NodeEnvelopeEditorControl editor =
			new(workspace, envelope, _ => { });
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph", BindingFlags.Instance
					| BindingFlags.NonPublic)!.GetValue(editor)!;
		Button match = (Button)typeof(NodeEnvelopeGraphControl)
			.GetField("_match", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		MethodInfo select = surface.GetType().GetMethod("Select")!;
		MethodInfo action = typeof(NodeEnvelopeGraphControl)
			.GetMethod("MatchSelected", BindingFlags.Instance
				| BindingFlags.NonPublic)!;
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();

		select.Invoke(surface, [1]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeTrue();
		double oldOpposite = envelope.Nodes[2].Value;
		TimeSpan oldTime = envelope.Nodes[1].Time;
		uint revision = workspace.Document.AudioRevision;
		action.Invoke(graph, []);
		envelope.Nodes[1].Value.Should().Be(oldOpposite);
		envelope.Nodes[1].Time.Should().Be(oldTime);
		envelope.Nodes[2].Value.Should().Be(oldOpposite);
		envelope.SustainStartIndex.Should().Be(1);
		envelope.SustainEndIndex.Should().Be(2);
		envelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.PingPong);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeFalse();
		action.Invoke(graph, []);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		select.Invoke(surface, [2]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeFalse();
		select.Invoke(surface, [0]);
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();
		select.Invoke(surface, [-1]);
		match.IsVisible.Should().BeFalse();
	}

	[AvaloniaTest]
	public void MatchCanCopyTheOtherDirectionAndHidesForSingleNodeSustain()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Two ends");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		Button match = (Button)typeof(NodeEnvelopeGraphControl)
			.GetField("_match", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		surface.GetType().GetMethod("Select")!.Invoke(surface, [2]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeTrue();
		uint revision = workspace.Document.AudioRevision;
		typeof(NodeEnvelopeGraphControl).GetMethod(
			"MatchSelected", BindingFlags.Instance
				| BindingFlags.NonPublic)!.Invoke(graph, []);
		envelope.Nodes[2].Value.Should().Be(1.0);
		envelope.Nodes[1].Value.Should().Be(1.0);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			2, 2, EnvelopeSustainLoopMode.Forward);
		graph.RefreshFromEnvelope();
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();
	}

	[AvaloniaTest]
	public void SustainBoundaryCommitUpdatesNumericFieldsAndOnlyOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Sustain");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.PingPong);
		NodeEnvelopeEditorControl editor =
			new(workspace, envelope, _ => { });
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph", BindingFlags.Instance
					| BindingFlags.NonPublic)!.GetValue(editor)!;
		MethodInfo commit = typeof(NodeEnvelopeGraphControl)
			.GetMethod("Commit", BindingFlags.Instance
				| BindingFlags.NonPublic)!;
		uint revision = workspace.Document.AudioRevision;
		NodeEnvelopeGraphEdit region = new(envelope.Nodes,
			SustainStartIndex: 0, SustainEndIndex: 3,
			SelectedNodeIndex: -1);
		commit.Invoke(graph, [region]);
		envelope.SustainStartIndex.Should().Be(0);
		envelope.SustainEndIndex.Should().Be(3);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		((TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_sustainStart", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(editor)!)
			.Text.Should().Be("0");
		((TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_sustainEnd", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(editor)!)
			.Text.Should().Be("3");
		commit.Invoke(graph, [region]);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}

	[AvaloniaTest]
	public void CancelledSustainDragRestoresPreviewWithoutDocumentMutation()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Cancel");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		Type type = surface.GetType();
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		uint revision = workspace.Document.AudioRevision;
		type.GetField("_dragGuide", flags)!.SetValue(surface, 0);
		type.GetField("_previewSustainStart", flags)!.SetValue(surface, 0);
		type.GetField("_previewSustainEnd", flags)!.SetValue(surface, 3);
		type.GetProperty("IsDragging")!.GetValue(surface)
			.Should().Be(true);
		type.GetMethod("CancelDrag")!.Invoke(surface, []);
		type.GetProperty("IsDragging")!.GetValue(surface)
			.Should().Be(false);
		type.GetField("_previewSustainStart", flags)!.GetValue(surface)
			.Should().Be(envelope.SustainStartIndex);
		type.GetField("_previewSustainEnd", flags)!.GetValue(surface)
			.Should().Be(envelope.SustainEndIndex);
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[AvaloniaTest]
	public void GraphDeletionOfSelectedNodePreservesSustainIndicesAndRevision()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Curve");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		uint revision = workspace.Document.AudioRevision;
		NodeEnvelopeGraphEdit deletion = NodeEnvelopeGraphGeometry.Delete(
			envelope.Nodes, 2, envelope.SustainStartIndex,
			envelope.SustainEndIndex)!;

		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [deletion]);
		envelope.Nodes.Should().HaveCount(3);
		envelope.SustainStartIndex.Should().Be(2);
		envelope.SustainEndIndex.Should().Be(2);
		graph.SelectedNodeIndex.Should().Be(-1);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}
}
