using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthWaypointDocumentTests
{
	[Test]
	public void InsertMoveAndDeleteWaypointsModifyOnlyEditorMetadata()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Waypoints");
		int output = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Add,
			[0], 400, 80);
		FmSynthGraph graph = synth.Graph;
		uint audioRevision = workspace.Document.AudioRevision;
		uint documentRevision = workspace.Document.DocumentRevision;
		FmSynthRoutePoint first = new(150, 110);
		FmSynthRoutePoint second = new(220, 180);

		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, output, 0, [first]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, output, 0,
			FmSynthWaypointGeometry.Insert([first], 1, second));
		FmSynthRoutePoint moved = new(240, 210);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, output, 0,
			FmSynthWaypointGeometry.Replace([first, second], 1, moved));

		synth.ConnectionRoutingHints.Should().ContainSingle()
			.Which.RoutePoints.Should().Equal(first, moved);
		synth.Graph.Should().BeSameAs(graph);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 3);

		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, output, 0,
			FmSynthWaypointGeometry.Remove([first, moved], 1));
		synth.ConnectionRoutingHints.Single()
			.RoutePoints.Should().Equal(first);
		FmSynthDocumentEditor.ClearRoutingHint(
			workspace, synth, output, 0);
		synth.ConnectionRoutingHints.Should().BeEmpty();
		synth.Graph.Should().BeSameAs(graph);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 5);
	}

	[Test]
	public void DeletingOneOfRepeatedSourceConnectionsPreservesOtherRoutes()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Parallel");
		int op = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Add,
			[0, 0, 0], 400, 80);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, op, 0,
			[new FmSynthRoutePoint(100, 100)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, op, 1,
			[new FmSynthRoutePoint(120, 120)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, op, 2,
			[new FmSynthRoutePoint(140, 140)]);

		FmSynthDocumentEditor.ClearRoutingHint(workspace, synth, op, 1);

		synth.ConnectionRoutingHints
			.Select(hint => hint.TargetInputIndex)
			.Should().Equal(0, 2);
		synth.ConnectionRoutingHints.Should().Contain(hint =>
			hint.TargetInputIndex == 2
				&& hint.RoutePoints.Single()
					== new FmSynthRoutePoint(140, 140));
	}
}
