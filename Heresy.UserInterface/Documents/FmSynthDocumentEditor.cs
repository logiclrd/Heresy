using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent mutation surface for persistent FM synth objects.
/// Semantic graph changes replace the immutable graph and advance AudioRevision;
/// editor-only layout/routing changes advance only DocumentRevision.
/// </summary>
public static class FmSynthDocumentEditor
{
	public static FmSynthDefinition CreateFmSynth(
		DocumentWorkspace workspace,
		string name)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		ObjectId id = workspace.Document.AllocateObjectId();
		FmSynthDefinition synth =
			new(
				id,
				name.Trim(),
				new FmSynthGraph(
					[
						new FmConstantNode(0, 0.0),
					],
					outputNodeId: 0));
		synth.NodePositions.Add(
			new FmSynthNodePosition(
				0,
				80.0,
				80.0));
		workspace.Document.Add(
			synth,
			affectsAudio: true);
		return synth;
	}

	public static int AddConstantNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		double value,
		double x,
		double y)
	{
		int id = NextNodeId(
			ValidateSynth(
				workspace,
				synth));
		AddNode(
			workspace,
			synth,
			new FmConstantNode(
				id,
				value),
			x,
			y);
		return id;
	}

	public static int AddOscillatorNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		FmOscillatorWaveform waveform,
		double frequencyHz,
		double x,
		double y,
		double minimum = -1.0,
		double maximum = 1.0,
		int? multiplierNodeId = null,
		bool exponentialMultiplier = false)
	{
		int id = NextNodeId(
			ValidateSynth(
				workspace,
				synth));
		AddNode(
			workspace,
			synth,
			new FmOscillatorNode(
				id,
				waveform,
				frequencyHz,
				minimum,
				maximum,
				multiplierNodeId,
				exponentialMultiplier),
			x,
			y);
		return id;
	}

	public static int AddEnvelopeNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		ObjectId envelopeId,
		double x,
		double y)
	{
		ValidateSynth(
			workspace,
			synth);
		ValidateEnvelope(
			workspace.Document,
			envelopeId);
		int id =
			NextNodeId(
				synth.Graph);
		AddNode(
			workspace,
			synth,
			new FmEnvelopeNode(
				id,
				envelopeId),
			x,
			y);
		return id;
	}

	public static int AddOperatorNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		FmOperatorKind operation,
		IEnumerable<int> inputNodeIds,
		double x,
		double y)
	{
		ArgumentNullException.ThrowIfNull(inputNodeIds);
		int id =
			NextNodeId(
				ValidateSynth(
					workspace,
					synth));
		AddNode(
			workspace,
			synth,
			new FmOperatorNode(
				id,
				operation,
				inputNodeIds),
			x,
			y);
		return id;
	}

	public static void UpdateNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		FmSynthNode replacement)
	{
		ArgumentNullException.ThrowIfNull(replacement);
		ValidateSynth(
			workspace,
			synth);
		if (!synth.Graph.Nodes.Any(
			node => node.Id == replacement.Id))
		{
			throw new InvalidOperationException(
				$"FM node {replacement.Id} does not exist.");
		}

		if (replacement is FmEnvelopeNode envelope)
		{
			ValidateEnvelope(
				workspace.Document,
				envelope.EnvelopeId);
		}

		FmSynthNode[] nodes =
			synth.Graph.Nodes
				.Select(node =>
					node.Id == replacement.Id
						? replacement
						: node)
				.ToArray();
		ReplaceGraph(
			workspace,
			synth,
			nodes,
			synth.Graph.OutputNodeId);
	}

	public static void SetOutputNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		int nodeId)
	{
		ValidateSynth(
			workspace,
			synth);
		RequireNode(
			synth,
			nodeId);
		if (synth.Graph.OutputNodeId == nodeId)
			return;

		ReplaceGraph(
			workspace,
			synth,
			synth.Graph.Nodes,
			nodeId);
	}

	public static void RemoveNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		int nodeId)
	{
		ValidateSynth(
			workspace,
			synth);
		RequireNode(
			synth,
			nodeId);

		if (synth.Graph.OutputNodeId == nodeId)
		{
			throw new InvalidOperationException(
				"The FM graph output node cannot be removed. Select another output first.");
		}

		if (synth.Graph.Nodes.Any(
			node =>
				node.Id != nodeId
				&& node.InputNodeIds.Contains(nodeId)))
		{
			throw new InvalidOperationException(
				$"FM node {nodeId} is still used as an input by another node.");
		}

		FmSynthNode[] nodes =
			synth.Graph.Nodes
				.Where(node => node.Id != nodeId)
				.ToArray();
		FmSynthGraph replacement =
			new(
				nodes,
				synth.Graph.OutputNodeId);

		synth.Graph = replacement;
		synth.NodePositions.RemoveAll(
			position => position.NodeId == nodeId);
		synth.ConnectionRoutingHints.RemoveAll(
			hint =>
				hint.SourceNodeId == nodeId
					|| hint.TargetNodeId == nodeId);
		PruneRoutingHints(synth);
		workspace.Document.MarkChanged(
			affectsAudio: true);
	}

	public static void MoveNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		int nodeId,
		double x,
		double y)
	{
		ValidateSynth(
			workspace,
			synth);
		RequireNode(
			synth,
			nodeId);
		FmSynthNodePosition position =
			new(
				nodeId,
				x,
				y);
		int index =
			synth.NodePositions.FindIndex(
				candidate =>
					candidate.NodeId == nodeId);
		if (index >= 0)
		{
			if (synth.NodePositions[index] == position)
				return;
			synth.NodePositions[index] = position;
		}
		else
		{
			synth.NodePositions.Add(position);
		}

		workspace.Document.MarkChanged(
			affectsAudio: false);
	}

	public static void SetRoutingHint(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		int sourceNodeId,
		int targetNodeId,
		int targetInputIndex,
		IEnumerable<FmSynthRoutePoint> routePoints)
	{
		ArgumentNullException.ThrowIfNull(routePoints);
		ValidateSynth(
			workspace,
			synth);
		if (!ConnectionExists(
			synth.Graph,
			sourceNodeId,
			targetNodeId,
			targetInputIndex))
		{
			throw new InvalidOperationException(
				"The routing hint must refer to an existing FM graph connection.");
		}

		FmSynthConnectionRoutingHint replacement =
			new(
				sourceNodeId,
				targetNodeId,
				targetInputIndex,
				routePoints);
		int index =
			synth.ConnectionRoutingHints.FindIndex(
				hint =>
					hint.TargetNodeId == targetNodeId
						&& hint.TargetInputIndex == targetInputIndex);
		if (index >= 0)
		{
			if (Equivalent(
				synth.ConnectionRoutingHints[index],
				replacement))
			{
				return;
			}
			synth.ConnectionRoutingHints[index] = replacement;
		}
		else
		{
			synth.ConnectionRoutingHints.Add(replacement);
		}

		workspace.Document.MarkChanged(
			affectsAudio: false);
	}

	public static void ClearRoutingHint(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		int targetNodeId,
		int targetInputIndex)
	{
		ValidateSynth(
			workspace,
			synth);
		int removed =
			synth.ConnectionRoutingHints.RemoveAll(
				hint =>
					hint.TargetNodeId == targetNodeId
						&& hint.TargetInputIndex == targetInputIndex);
		if (removed != 0)
		{
			workspace.Document.MarkChanged(
				affectsAudio: false);
		}
	}

	private static void AddNode(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		FmSynthNode node,
		double x,
		double y)
	{
		FmSynthNodePosition position =
			new(
				node.Id,
				x,
				y);
		FmSynthNode[] nodes =
			synth.Graph.Nodes
				.Concat([node])
				.ToArray();
		FmSynthGraph replacement =
			new(
				nodes,
				synth.Graph.OutputNodeId);

		synth.Graph = replacement;
		synth.NodePositions.Add(position);
		PruneRoutingHints(synth);
		workspace.Document.MarkChanged(
			affectsAudio: true);
	}

	private static void ReplaceGraph(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		IEnumerable<FmSynthNode> nodes,
		int outputNodeId)
	{
		FmSynthGraph replacement =
			new(
				nodes,
				outputNodeId);
		synth.Graph = replacement;
		PruneRoutingHints(synth);
		workspace.Document.MarkChanged(
			affectsAudio: true);
	}

	private static FmSynthGraph ValidateSynth(
		DocumentWorkspace workspace,
		FmSynthDefinition synth)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(synth);
		if (!workspace.Document.TryGet(
			synth.Id,
			out SongObject? stored)
			|| !ReferenceEquals(
				stored,
				synth))
		{
			throw new InvalidOperationException(
				"The FM synth is not part of the active song document.");
		}
		return synth.Graph;
	}

	private static void ValidateEnvelope(
		SongDocument document,
		ObjectId envelopeId)
	{
		if (envelopeId.IsNone
			|| !document.TryGet(
				envelopeId,
				out SongObject? songObject)
			|| songObject is not EnvelopeDefinition)
		{
			throw new InvalidOperationException(
				$"Object {envelopeId.Value} is not a live envelope in the active song.");
		}
	}

	private static FmSynthNode RequireNode(
		FmSynthDefinition synth,
		int nodeId)
		=> synth.Graph.Nodes.FirstOrDefault(
			node => node.Id == nodeId)
			?? throw new InvalidOperationException(
				$"FM node {nodeId} does not exist.");

	private static int NextNodeId(
		FmSynthGraph graph)
	{
		int maximum =
			graph.Nodes.Max(
				node => node.Id);
		if (maximum == int.MaxValue)
		{
			throw new InvalidOperationException(
				"The FM graph has exhausted its node ID space.");
		}
		return maximum + 1;
	}

	private static void PruneRoutingHints(
		FmSynthDefinition synth)
	{
		synth.ConnectionRoutingHints.RemoveAll(
			hint =>
				!ConnectionExists(
					synth.Graph,
					hint.SourceNodeId,
					hint.TargetNodeId,
					hint.TargetInputIndex));
	}

	private static bool ConnectionExists(
		FmSynthGraph graph,
		int sourceNodeId,
		int targetNodeId,
		int targetInputIndex)
	{
		FmSynthNode? target =
			graph.Nodes.FirstOrDefault(
				node =>
					node.Id == targetNodeId);
		return target is not null
			&& targetInputIndex >= 0
			&& targetInputIndex < target.InputNodeIds.Count
			&& target.InputNodeIds[targetInputIndex] == sourceNodeId;
	}

	private static bool Equivalent(
		FmSynthConnectionRoutingHint left,
		FmSynthConnectionRoutingHint right)
		=> left.SourceNodeId == right.SourceNodeId
			&& left.TargetNodeId == right.TargetNodeId
			&& left.TargetInputIndex == right.TargetInputIndex
			&& left.RoutePoints.SequenceEqual(
				right.RoutePoints);
}
