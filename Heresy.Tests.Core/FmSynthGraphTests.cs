using System;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmSynthGraphTests
{
	[Test]
	public void GraphAcceptsValidatedDagAndPreservesDefinitionOrder()
	{
		FmSynthGraph graph =
			new(
				[
					new FmConstantNode(7, 2.0),
					new FmOscillatorNode(
						3,
						FmOscillatorWaveform.Sine,
						frequencyHz: 440.0,
						multiplierNodeId: 7),
					new FmOperatorNode(
						9,
						FmOperatorKind.Multiply,
						[3, 7]),
				],
				outputNodeId: 9);

		graph.Nodes.Should().HaveCount(3);
		graph.Nodes[0].Id.Should().Be(7);
		graph.Nodes[1].Id.Should().Be(3);
		graph.Nodes[2].Id.Should().Be(9);
		graph.OutputNodeId.Should().Be(9);
	}

	[Test]
	public void DuplicateNodeIdsAreRejected()
	{
		Action action =
			() => new FmSynthGraph(
				[
					new FmConstantNode(1, 1.0),
					new FmConstantNode(1, 2.0),
				],
				outputNodeId: 1);

		action.Should()
			.Throw<ArgumentException>()
			.WithMessage("*duplicate*");
	}

	[Test]
	public void MissingConnectionIsRejected()
	{
		Action action =
			() => new FmSynthGraph(
				[
					new FmOscillatorNode(
						1,
						FmOscillatorWaveform.Sine,
						440.0,
						multiplierNodeId: 99),
				],
				outputNodeId: 1);

		action.Should()
			.Throw<ArgumentException>()
			.WithMessage("*99*");
	}

	[Test]
	public void MissingOutputNodeIsRejected()
	{
		Action action =
			() => new FmSynthGraph(
				[
					new FmConstantNode(1, 1.0),
				],
				outputNodeId: 99);

		action.Should()
			.Throw<ArgumentException>()
			.WithMessage("*output*");
	}

	[Test]
	public void CyclesAreRejected()
	{
		Action action =
			() => new FmSynthGraph(
				[
					new FmOperatorNode(
						1,
						FmOperatorKind.Add,
						[2]),
					new FmOperatorNode(
						2,
						FmOperatorKind.Add,
						[1]),
				],
				outputNodeId: 1);

		action.Should()
			.Throw<ArgumentException>()
			.WithMessage("*cycle*");
	}

	[Test]
	public void OscillatorParametersMustBeFiniteAndFrequencyPositive()
	{
		Action zeroFrequency =
			() => new FmOscillatorNode(
				1,
				FmOscillatorWaveform.Sine,
				frequencyHz: 0.0);
		zeroFrequency.Should()
			.Throw<ArgumentOutOfRangeException>();

		Action nonFiniteMinimum =
			() => new FmOscillatorNode(
				1,
				FmOscillatorWaveform.Sine,
				frequencyHz: 440.0,
				minimum: double.NaN);
		nonFiniteMinimum.Should()
			.Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void ConstantMustBeFinite()
	{
		Action action =
			() => new FmConstantNode(
				1,
				double.PositiveInfinity);

		action.Should()
			.Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void EnvelopeRequiresConcreteObjectId()
	{
		Action action =
			() => new FmEnvelopeNode(
				1,
				ObjectId.None);

		action.Should()
			.Throw<ArgumentException>();
	}

	[Test]
	public void OperatorRequiresAtLeastOneInput()
	{
		Action action =
			() => new FmOperatorNode(
				1,
				FmOperatorKind.Add,
				[]);

		action.Should()
			.Throw<ArgumentException>();
	}
}
