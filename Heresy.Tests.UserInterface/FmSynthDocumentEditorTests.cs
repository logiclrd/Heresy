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
	public void RemovingReferencedOrOutputNodeIsRejectedButUnusedNodeCanBeRemoved()
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
		referenced.Should().Throw<InvalidOperationException>();

		FmSynthDocumentEditor.RemoveNode(
			workspace,
				synth,
				unused);

		synth.Graph.Nodes.Should().NotContain(node => node.Id == unused);
		synth.NodePositions.Should().NotContain(position => position.NodeId == unused);
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
