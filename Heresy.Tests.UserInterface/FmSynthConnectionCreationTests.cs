using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConnectionCreationTests
{
	[Test]
	public void ConnectionPortsExposeOutputAndTypedInputSlots()
	{
		FmSynthLayoutRect bounds = new(100.0, 200.0, 170.0, 72.0);
		FmSynthConnectionPort[] constantPorts =
			FmSynthConnectionPorts.GetPorts(new FmConstantNode(0, 1.0), bounds);
		constantPorts.Should().ContainSingle()
			.Which.Kind.Should().Be(FmSynthConnectionPortKind.Output);

		FmSynthConnectionPort[] oscillatorPorts =
			FmSynthConnectionPorts.GetPorts(
				new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 440.0),
				bounds);
		oscillatorPorts.Should().HaveCount(2);
		oscillatorPorts[0].Center.Should().Be(new FmSynthRoutePoint(270.0, 236.0));
		oscillatorPorts[1].Kind.Should().Be(FmSynthConnectionPortKind.Input);
		oscillatorPorts[1].InputIndex.Should().Be(0);
		oscillatorPorts[1].Center.Should().Be(new FmSynthRoutePoint(100.0, 236.0));

		FmSynthConnectionPort[] operatorPorts =
			FmSynthConnectionPorts.GetPorts(
				new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
				bounds);
		operatorPorts.Should().HaveCount(4);
		operatorPorts.Where(port => port.Kind == FmSynthConnectionPortKind.Input)
			.Select(port => port.InputIndex).Should().Equal(0, 1, 2);
		operatorPorts[3].Center.X.Should().Be(bounds.Left);
	}

	[Test]
	public void PortHitTestRecognizesNearbyHandlesAndRejectsEmptySpace()
	{
		FmSynthConnectionPort[] ports =
			FmSynthConnectionPorts.GetPorts(
				new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 440.0),
				new FmSynthLayoutRect(100.0, 200.0, 170.0, 72.0));

		FmSynthConnectionPorts.HitTest(
				ports,
				new FmSynthRoutePoint(272.0, 238.0))
			.Should().Be(ports[0]);
		FmSynthConnectionPorts.HitTest(
				ports,
				new FmSynthRoutePoint(200.0, 236.0))
			.Should().BeNull();
	}

	[Test]
	public void DragConnectionCanStartAtEitherOutputOrInput()
	{
		FmSynthConnectionPort output =
			new(1, FmSynthConnectionPortKind.Output, -1, new FmSynthRoutePoint(0, 0));
		FmSynthConnectionPort input =
			new(2, FmSynthConnectionPortKind.Input, 0, new FmSynthRoutePoint(10, 0));

		FmSynthConnectionPorts.TryResolve(
				output,
				input,
				out FmSynthConnectionCandidate forward)
			.Should().BeTrue();
		forward.Should().Be(new FmSynthConnectionCandidate(1, 2, 0));
		FmSynthConnectionPorts.TryResolve(
				input,
				output,
				out FmSynthConnectionCandidate reversed)
			.Should().BeTrue();
		reversed.Should().Be(forward);

		FmSynthConnectionPorts.TryResolve(output, output, out _).Should().BeFalse();
		FmSynthConnectionPorts.TryResolve(input, input, out _).Should().BeFalse();
		FmSynthConnectionPorts.TryResolve(
				output,
				input with { NodeId = 1 },
				out _)
			.Should().BeFalse();
	}

	[Test]
	public void ConnectToOscillatorAndReplaceExistingInputWithoutDanglingHint()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Connections");
		int second =
			FmSynthDocumentEditor.AddConstantNode(workspace, synth, 2.0, 200.0, 100.0);
		int oscillator =
			FmSynthDocumentEditor.AddOscillatorNode(
				workspace, synth, FmOscillatorWaveform.Sine, 440.0, 400.0, 100.0);
		uint audioRevision = workspace.Document.AudioRevision;

		FmSynthDocumentEditor.ConnectNodes(workspace, synth, 0, oscillator, 0);
		FmOscillatorNode connected =
			synth.Graph.Nodes.OfType<FmOscillatorNode>().Single();
		connected.MultiplierNodeId.Should().Be(0);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
		FmSynthDocumentEditor.SetRoutingHint(
			workspace, synth, 0, oscillator, 0,
			[new FmSynthRoutePoint(300.0, 200.0)]);
		uint revision = workspace.Document.AudioRevision;
		FmSynthDocumentEditor.ConnectNodes(workspace, synth, 0, oscillator, 0);
		workspace.Document.AudioRevision.Should().Be(revision);

		FmSynthDocumentEditor.ConnectNodes(workspace, synth, second, oscillator, 0);
		synth.Graph.Nodes.OfType<FmOscillatorNode>().Single()
			.MultiplierNodeId.Should().Be(second);
		synth.ConnectionRoutingHints.Should().BeEmpty();
	}

	[Test]
	public void OperatorAllowsReplacingSlotsAndAppendingInputs()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Operators");
		int second =
			FmSynthDocumentEditor.AddConstantNode(workspace, synth, 2.0, 200.0, 100.0);
		int operation =
			FmSynthDocumentEditor.AddOperatorNode(
				workspace, synth, FmOperatorKind.Add, [0], 400.0, 100.0);
		uint revision = workspace.Document.AudioRevision;

		FmSynthDocumentEditor.ConnectNodes(workspace, synth, second, operation, 1);
		synth.Graph.Nodes.OfType<FmOperatorNode>().Single()
			.InputNodeIds.Should().Equal(0, second);
		FmSynthDocumentEditor.ConnectNodes(workspace, synth, second, operation, 0);
		synth.Graph.Nodes.OfType<FmOperatorNode>().Single()
			.InputNodeIds.Should().Equal(second, second);
		workspace.Document.AudioRevision.Should().Be(revision + 2);
	}

	[Test]
	public void InvalidConnectionsLeaveDocumentAndGraphUnmodified()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Invalid");
		int oscillator =
			FmSynthDocumentEditor.AddOscillatorNode(
				workspace, synth, FmOscillatorWaveform.Sine, 440.0, 300.0, 100.0,
				multiplierNodeId: 0);
		FmSynthGraph before = synth.Graph;
		uint revision = workspace.Document.DocumentRevision;

		Action self = () =>
			FmSynthDocumentEditor.ConnectNodes(workspace, synth, oscillator, oscillator, 0);
		Action cycle = () =>
			FmSynthDocumentEditor.ConnectNodes(workspace, synth, oscillator, 0, 0);
		Action nonConsumer = () =>
			FmSynthDocumentEditor.ConnectNodes(workspace, synth, 0, 0, 0);
		Action invalidSlot = () =>
			FmSynthDocumentEditor.ConnectNodes(workspace, synth, 0, oscillator, 1);
		self.Should().Throw<Exception>();
		cycle.Should().Throw<Exception>();
		nonConsumer.Should().Throw<Exception>();
		invalidSlot.Should().Throw<Exception>();
		synth.Graph.Should().BeSameAs(before);
		workspace.Document.DocumentRevision.Should().Be(revision);
	}
}
