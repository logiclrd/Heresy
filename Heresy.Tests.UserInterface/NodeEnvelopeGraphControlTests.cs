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
