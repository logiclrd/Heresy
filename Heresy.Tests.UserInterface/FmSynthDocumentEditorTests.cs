using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthDocumentEditorTests
{
	[Test]
	public void CreateFmSynthStartsWithSilentOutputAndCanonicalSamplePlacement()
	{
		DocumentWorkspace workspace = new();

		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(
				workspace,
				"FM Bell");

		synth.Name.Should().Be("FM Bell");
		synth.Graph.OutputNodeId.Should().Be(0);
		synth.Graph.Nodes.Should().ContainSingle()
			.Which.Should().BeOfType<FmConstantNode>()
			.Which.Value.Should().Be(0.0);
		synth.NodePositions.Should().Equal(
			new FmSynthNodePosition(0, 80.0, 80.0));
		workspace.Document.GetSectionRoot(SongTreeSection.Samples)
			.Children.Cast<SongTreeObject>()
			.Select(node => node.ObjectId)
			.Should().Contain(synth.Id);
		workspace.Document.AudioRevision.Should().Be(1);
	}

	[Test]
	public void SemanticNodeEditsReplaceImmutableGraphAndAdvanceAudioRevision()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		uint revision = workspace.Document.AudioRevision;

		int oscillatorId =
			FmSynthDocumentEditor.AddOscillatorNode(
				workspace,
				synth,
				FmOscillatorWaveform.Sine,
				frequencyHz: 440.0,
				x: 260.0,
				y: 80.0);
		FmSynthDocumentEditor.UpdateNode(
			workspace,
				synth,
				new FmOscillatorNode(
					oscillatorId,
					FmOscillatorWaveform.Square,
					frequencyHz: 220.0,
					minimum: -0.5,
					maximum: 0.75,
					multiplierNodeId: 0,
					exponentialMultiplier: true));
		FmSynthDocumentEditor.SetOutputNode(
			workspace,
				synth,
				oscillatorId);

		synth.Graph.OutputNodeId.Should().Be(oscillatorId);
		FmOscillatorNode oscillator =
			synth.Graph.Nodes.Single(node => node.Id == oscillatorId)
				.Should().BeOfType<FmOscillatorNode>().Subject;
		oscillator.Waveform.Should().Be(FmOscillatorWaveform.Square);
		oscillator.FrequencyHz.Should().Be(220.0);
		oscillator.MultiplierNodeId.Should().Be(0);
		oscillator.ExponentialMultiplier.Should().BeTrue();
		workspace.Document.AudioRevision.Should().Be(revision + 3);
	}

	[Test]
	public void EnvelopeNodesRequireLiveEnvelopeObjects()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");

		Action missing = () =>
			FmSynthDocumentEditor.AddEnvelopeNode(
				workspace,
				synth,
				(ObjectId)999U,
				x: 200.0,
				y: 100.0);
		missing.Should().Throw<InvalidOperationException>();

		ObjectId envelopeId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new AdsrEnvelopeDefinition(
				envelopeId,
				"Amp"));

		int nodeId =
			FmSynthDocumentEditor.AddEnvelopeNode(
				workspace,
				synth,
				envelopeId,
				x: 200.0,
				y: 100.0);

		synth.Graph.Nodes.Single(node => node.Id == nodeId)
			.Should().BeOfType<FmEnvelopeNode>()
			.Which.EnvelopeId.Should().Be(envelopeId);
	}

	[Test]
	public void MovingNodeAndChangingRouteHintsAreDocumentOnlyEdits()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int opId =
			FmSynthDocumentEditor.AddOperatorNode(
				workspace,
				synth,
				FmOperatorKind.Add,
				[0],
				x: 300.0,
				y: 80.0);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		FmSynthDocumentEditor.MoveNode(
			workspace,
				synth,
				nodeId: 0,
				x: 120.0,
				y: 160.0);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace,
				synth,
				sourceNodeId: 0,
				targetNodeId: opId,
				targetInputIndex: 0,
				routePoints:
				[
					new FmSynthRoutePoint(220.0, 160.0),
				]);

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 2);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
		synth.NodePositions.Single(position => position.NodeId == 0)
			.Should().Be(new FmSynthNodePosition(0, 120.0, 160.0));
		synth.ConnectionRoutingHints.Should().ContainSingle();
	}

	[Test]
	public void RoutingHintMustDescribeAnActualConnection()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int constant =
			FmSynthDocumentEditor.AddConstantNode(
				workspace,
				synth,
				1.0,
				200.0,
				200.0);
		int opId =
			FmSynthDocumentEditor.AddOperatorNode(
				workspace,
				synth,
				FmOperatorKind.Add,
				[0],
				x: 300.0,
				y: 80.0);

		Action action = () =>
			FmSynthDocumentEditor.SetRoutingHint(
				workspace,
				synth,
				constant,
				opId,
				targetInputIndex: 0,
				routePoints: []);

		action.Should().Throw<InvalidOperationException>();
		synth.ConnectionRoutingHints.Should().BeEmpty();
	}

	[Test]
	public void RemovingReferencedNodeReplacesEmptyOutputOperatorWithZero()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int unused =
			FmSynthDocumentEditor.AddConstantNode(
				workspace,
				synth,
				1.0,
				200.0,
				200.0);
		int opId =
			FmSynthDocumentEditor.AddOperatorNode(
				workspace,
				synth,
				FmOperatorKind.Add,
				[0],
				x: 300.0,
				y: 80.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, opId);

		Action output = () =>
			FmSynthDocumentEditor.RemoveNode(
				workspace,
				synth,
				opId);
		Action referenced = () =>
			FmSynthDocumentEditor.RemoveNode(
				workspace,
				synth,
				0);

		output.Should().Throw<InvalidOperationException>();
		referenced.Should().NotThrow();
		synth.Graph.OutputNodeId.Should().Be(opId);
		synth.Graph.Nodes.Should().NotContain(node => node.Id == 0);
		synth.Graph.Nodes.Single(node => node.Id == opId)
		.Should().BeOfType<FmConstantNode>()
		.Which.Value.Should().Be(0.0);

		FmSynthDocumentEditor.RemoveNode(
			workspace,
				synth,
				unused);

		synth.Graph.Nodes.Should().NotContain(node => node.Id == unused);
		synth.NodePositions.Should().NotContain(position => position.NodeId == unused);
	}

	[Test]
	public void RemovingConsumedNodeDisconnectsAllOccurrencesAndReindexesRoutingHints()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int removed = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 1.0, 100.0, 200.0);
		int first = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 2.0, 200.0, 200.0);
		int last = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 3.0, 300.0, 200.0);
		int op = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Add,
			[removed, first, removed, last], 450.0, 200.0);
		int downstream = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Multiply,
			[op, 0], 700.0, 200.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, downstream);

		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, removed, op, 0,
			[new FmSynthRoutePoint(10.0, 10.0)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, first, op, 1,
			[new FmSynthRoutePoint(20.0, 20.0)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, removed, op, 2,
			[new FmSynthRoutePoint(30.0, 30.0)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, last, op, 3,
			[new FmSynthRoutePoint(40.0, 40.0)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, downstream, 1,
			[new FmSynthRoutePoint(50.0, 50.0)]);

		FmSynthGraph before = synth.Graph;
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;
		FmSynthDocumentEditor.RemoveNode(workspace, synth, removed);

		synth.Graph.Should().NotBeSameAs(before);
		synth.Graph.Nodes.Should().NotContain(node => node.Id == removed);
		synth.Graph.Nodes.Single(node => node.Id == op)
		.Should().BeOfType<FmOperatorNode>()
		.Which.InputNodeIds.Should().Equal(first, last);
		synth.Graph.Nodes.Single(node => node.Id == downstream)
		.InputNodeIds.Should().Equal(op, 0);
		synth.NodePositions.Should().NotContain(p => p.NodeId == removed);
		synth.ConnectionRoutingHints.Should().HaveCount(3);
		synth.ConnectionRoutingHints.Should().ContainEquivalentOf(
			new FmSynthConnectionRoutingHint(
				first, op, 0, [new FmSynthRoutePoint(20.0, 20.0)]));
		synth.ConnectionRoutingHints.Should().ContainEquivalentOf(
			new FmSynthConnectionRoutingHint(
				last, op, 1, [new FmSynthRoutePoint(40.0, 40.0)]));
		synth.ConnectionRoutingHints.Should().ContainEquivalentOf(
			new FmSynthConnectionRoutingHint(
				0, downstream, 1, [new FmSynthRoutePoint(50.0, 50.0)]));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void RemovingMultiplierSourcePreservesOscillatorAndUnrelatedConnections()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int removed = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 1.0, 100.0, 200.0);
		int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
			workspace, synth, FmOscillatorWaveform.Square, 330.0,
			300.0, 200.0, minimum: -0.5, maximum: 0.75,
			multiplierNodeId: removed, exponentialMultiplier: true);
		int op = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Add, [oscillator],
			500.0, 200.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, op);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, removed, oscillator, 0,
			[new FmSynthRoutePoint(10.0, 10.0)]);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, oscillator, op, 0,
			[new FmSynthRoutePoint(20.0, 20.0)]);

		FmSynthDocumentEditor.RemoveNode(workspace, synth, removed);

		FmOscillatorNode remaining = synth.Graph.Nodes
			.OfType<FmOscillatorNode>().Single();
		remaining.MultiplierNodeId.Should().BeNull();
		remaining.Waveform.Should().Be(FmOscillatorWaveform.Square);
		remaining.FrequencyHz.Should().Be(330.0);
		remaining.Minimum.Should().Be(-0.5);
		remaining.Maximum.Should().Be(0.75);
		remaining.ExponentialMultiplier.Should().BeTrue();
		synth.ConnectionRoutingHints.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(
				new FmSynthConnectionRoutingHint(
					oscillator, op, 0,
					[new FmSynthRoutePoint(20.0, 20.0)]));
	}

	[Test]
	public void EmptyOperatorIsSilencedWithoutDeletingDownstreamConsumers()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int removed = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 1.0, 100.0, 200.0);
		int op = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Multiply,
			[removed, removed], 300.0, 200.0);
		int downstream = FmSynthDocumentEditor.AddOperatorNode(
			workspace, synth, FmOperatorKind.Add, [op], 500.0, 200.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, downstream);

		FmSynthDocumentEditor.RemoveNode(workspace, synth, removed);

		synth.Graph.Nodes.Single(node => node.Id == op)
			.Should().BeOfType<FmConstantNode>()
			.Which.Value.Should().Be(0.0);
		synth.Graph.Nodes.Single(node => node.Id == downstream)
			.InputNodeIds.Should().Equal(op);
		synth.Graph.OutputNodeId.Should().Be(downstream);
	}

	[Test]
	public void FailedRemovalDoesNotTouchRevisionsLayoutOrRouting()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int extra = FmSynthDocumentEditor.AddConstantNode(
			workspace, synth, 1.0, 200.0, 200.0);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, extra);
		FmSynthGraph graph = synth.Graph;
		uint revision = workspace.Document.DocumentRevision;
		uint audio = workspace.Document.AudioRevision;
		FmSynthNodePosition[] positions = [.. synth.NodePositions];

		Action removeOutput = () =>
			FmSynthDocumentEditor.RemoveNode(workspace, synth, extra);
		Action removeMissing = () =>
			FmSynthDocumentEditor.RemoveNode(workspace, synth, 99999);

		removeOutput.Should().Throw<InvalidOperationException>();
		removeMissing.Should().Throw<InvalidOperationException>();
		synth.Graph.Should().BeSameAs(graph);
		synth.NodePositions.Should().Equal(positions);
		workspace.Document.DocumentRevision.Should().Be(revision);
		workspace.Document.AudioRevision.Should().Be(audio);
	}

	[Test]
	public void SemanticGraphReplacementPrunesStaleRoutingHints()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int oscillatorId =
			FmSynthDocumentEditor.AddOscillatorNode(
				workspace,
				synth,
				FmOscillatorWaveform.Sine,
				frequencyHz: 440.0,
				x: 300.0,
				y: 80.0,
				multiplierNodeId: 0);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace,
			synth,
			0,
			oscillatorId,
			0,
			[new FmSynthRoutePoint(200.0, 80.0)]);

		FmSynthDocumentEditor.UpdateNode(
			workspace,
			synth,
			new FmOscillatorNode(
				oscillatorId,
				FmOscillatorWaveform.Sine,
				frequencyHz: 440.0));

		synth.ConnectionRoutingHints.Should().BeEmpty();
	}

	[Test]
	public void InvalidReplacementDoesNotPartiallyMutateGraphOrHints()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int opId =
			FmSynthDocumentEditor.AddOperatorNode(
				workspace,
				synth,
				FmOperatorKind.Add,
				[0],
				x: 300.0,
				y: 80.0);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace,
				synth,
				0,
				opId,
				0,
				[new FmSynthRoutePoint(200.0, 80.0)]);
		FmSynthGraph before = synth.Graph;

		Action action = () =>
			FmSynthDocumentEditor.UpdateNode(
				workspace,
				synth,
				new FmOperatorNode(
					opId,
					FmOperatorKind.Add,
					[opId]));

		action.Should().Throw<ArgumentException>();
		synth.Graph.Should().BeSameAs(before);
		synth.ConnectionRoutingHints.Should().ContainSingle();
	}
}
