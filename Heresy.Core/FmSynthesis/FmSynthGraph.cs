using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.Core.FmSynthesis;

public enum FmOscillatorWaveform
{
	Sine,
	Triangle,
	Sawtooth,
	Square,
}

public enum FmOperatorKind
{
	Add,
	Multiply,
	Minimum,
	Maximum,
}

public abstract class FmSynthNode
{
	protected FmSynthNode(int id)
	{
		if (id < 0)
			throw new ArgumentOutOfRangeException(nameof(id));

		Id = id;
	}

	public int Id { get; }

	public abstract IReadOnlyList<int> InputNodeIds { get; }
}

public sealed class FmConstantNode : FmSynthNode
{
	public FmConstantNode(
		int id,
		double value)
		: base(id)
	{
		if (!double.IsFinite(value))
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public double Value { get; }

	public override IReadOnlyList<int> InputNodeIds { get; } =
		Array.Empty<int>();
}

public sealed class FmOscillatorNode : FmSynthNode
{
	private readonly IReadOnlyList<int> _inputs;

	public FmOscillatorNode(
		int id,
		FmOscillatorWaveform waveform,
		double frequencyHz,
		double minimum = -1.0,
		double maximum = 1.0,
		int? multiplierNodeId = null,
		bool exponentialMultiplier = false)
		: base(id)
	{
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (!(frequencyHz > 0.0)
			|| !double.IsFinite(frequencyHz))
		{
			throw new ArgumentOutOfRangeException(nameof(frequencyHz));
		}
		if (!double.IsFinite(minimum))
			throw new ArgumentOutOfRangeException(nameof(minimum));
		if (!double.IsFinite(maximum))
			throw new ArgumentOutOfRangeException(nameof(maximum));
		if (multiplierNodeId.HasValue
			&& multiplierNodeId.Value < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(multiplierNodeId));
		}

		Waveform = waveform;
		FrequencyHz = frequencyHz;
		Minimum = minimum;
		Maximum = maximum;
		MultiplierNodeId = multiplierNodeId;
		ExponentialMultiplier = exponentialMultiplier;
		_inputs =
			Array.AsReadOnly(
				multiplierNodeId.HasValue
					? [multiplierNodeId.Value]
					: Array.Empty<int>());
	}

	public FmOscillatorWaveform Waveform { get; }
	public double FrequencyHz { get; }
	public double Minimum { get; }
	public double Maximum { get; }
	public int? MultiplierNodeId { get; }
	public bool ExponentialMultiplier { get; }

	public override IReadOnlyList<int> InputNodeIds => _inputs;
}

public sealed class FmEnvelopeNode : FmSynthNode
{
	public FmEnvelopeNode(
		int id,
		ObjectId envelopeId)
		: base(id)
	{
		if (envelopeId.IsNone)
			throw new ArgumentException(
				"Envelope nodes require a concrete object ID.",
				nameof(envelopeId));

		EnvelopeId = envelopeId;
	}

	public ObjectId EnvelopeId { get; }

	public override IReadOnlyList<int> InputNodeIds { get; } =
		Array.Empty<int>();
}

public sealed class FmOperatorNode : FmSynthNode
{
	private readonly int[] _inputs;

	public FmOperatorNode(
		int id,
		FmOperatorKind operation,
		IEnumerable<int> inputNodeIds)
		: base(id)
	{
		if (!Enum.IsDefined(operation))
			throw new ArgumentOutOfRangeException(nameof(operation));
		ArgumentNullException.ThrowIfNull(inputNodeIds);

		int[] inputs = inputNodeIds.ToArray();
		if (inputs.Length == 0)
		{
			throw new ArgumentException(
				"FM operator nodes require at least one input.",
				nameof(inputNodeIds));
		}
		if (inputs.Any(input => input < 0))
		{
			throw new ArgumentOutOfRangeException(
				nameof(inputNodeIds));
		}

		_inputs = Array.AsReadOnly(inputs);
		Operation = operation;
	}

	public FmOperatorKind Operation { get; }

	public override IReadOnlyList<int> InputNodeIds => _inputs;
}

public sealed class FmSynthGraph
{
	private readonly IReadOnlyList<FmSynthNode> _nodes;

	public FmSynthGraph(
		IEnumerable<FmSynthNode> nodes,
		int outputNodeId)
	{
		ArgumentNullException.ThrowIfNull(nodes);

		FmSynthNode[] nodeArray = nodes.ToArray();
		if (nodeArray.Any(node => node is null))
		{
			throw new ArgumentException(
				"FM graph nodes may not contain null.",
				nameof(nodes));
		}

		_nodes = Array.AsReadOnly(nodeArray);

		Dictionary<int, FmSynthNode> byId = [];
		foreach (FmSynthNode node in _nodes)
		{
			if (!byId.TryAdd(node.Id, node))
			{
				throw new ArgumentException(
					$"FM graph contains duplicate node ID {node.Id}.",
					nameof(nodes));
			}
		}

		if (!byId.ContainsKey(outputNodeId))
		{
			throw new ArgumentException(
				$"FM graph output node {outputNodeId} does not exist.",
				nameof(outputNodeId));
		}

		foreach (FmSynthNode node in _nodes)
		{
			foreach (int inputId in node.InputNodeIds)
			{
				if (!byId.ContainsKey(inputId))
				{
					throw new ArgumentException(
						$"FM graph node {node.Id} references missing input node {inputId}.",
						nameof(nodes));
				}
			}
		}

		ValidateAcyclic(byId);

		OutputNodeId = outputNodeId;
	}

	public IReadOnlyList<FmSynthNode> Nodes => _nodes;

	public int OutputNodeId { get; }

	private static void ValidateAcyclic(
		IReadOnlyDictionary<int, FmSynthNode> nodes)
	{
		Dictionary<int, byte> marks = [];

		foreach (int id in nodes.Keys)
			Visit(id);

		void Visit(int id)
		{
			if (marks.TryGetValue(id, out byte mark))
			{
				if (mark == 1)
				{
					throw new ArgumentException(
						"FM graph contains a cycle.",
						nameof(nodes));
				}
				if (mark == 2)
					return;
			}

			marks[id] = 1;
			foreach (int input in nodes[id].InputNodeIds)
				Visit(input);
			marks[id] = 2;
		}
	}
}
