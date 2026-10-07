using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.Core.FmSynthesis;

/// <summary>
/// Editor-only position for an FM graph node. This metadata is persisted with
/// the song object but is deliberately not part of <see cref="FmSynthGraph"/>.
/// </summary>
public readonly record struct FmSynthNodePosition
{
	public FmSynthNodePosition(
		int nodeId,
		double x,
		double y)
	{
		if (nodeId < 0)
			throw new ArgumentOutOfRangeException(nameof(nodeId));
		if (!double.IsFinite(x))
			throw new ArgumentOutOfRangeException(nameof(x));
		if (!double.IsFinite(y))
			throw new ArgumentOutOfRangeException(nameof(y));

		NodeId = nodeId;
		X = x;
		Y = y;
	}

	public int NodeId { get; }
	public double X { get; }
	public double Y { get; }
}

/// <summary>
/// Editor-only orthogonal-routing waypoint. Routing hints never participate in
/// graph evaluation.
/// </summary>
public readonly record struct FmSynthRoutePoint
{
	public FmSynthRoutePoint(
		double x,
		double y)
	{
		if (!double.IsFinite(x))
			throw new ArgumentOutOfRangeException(nameof(x));
		if (!double.IsFinite(y))
			throw new ArgumentOutOfRangeException(nameof(y));

		X = x;
		Y = y;
	}

	public double X { get; }
	public double Y { get; }
}

/// <summary>
/// Optional editor-only routing information for one graph connection. The
/// source/target identity is retained so stale hints can be ignored safely if a
/// later graph edit changes connection topology.
/// </summary>
public sealed class FmSynthConnectionRoutingHint
{
	private readonly IReadOnlyList<FmSynthRoutePoint> _routePoints;

	public FmSynthConnectionRoutingHint(
		int sourceNodeId,
		int targetNodeId,
		int targetInputIndex,
		IEnumerable<FmSynthRoutePoint> routePoints)
	{
		if (sourceNodeId < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceNodeId));
		if (targetNodeId < 0)
			throw new ArgumentOutOfRangeException(nameof(targetNodeId));
		if (targetInputIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(targetInputIndex));
		ArgumentNullException.ThrowIfNull(routePoints);

		SourceNodeId = sourceNodeId;
		TargetNodeId = targetNodeId;
		TargetInputIndex = targetInputIndex;
		_routePoints =
			Array.AsReadOnly(
				routePoints.ToArray());
	}

	public int SourceNodeId { get; }
	public int TargetNodeId { get; }
	public int TargetInputIndex { get; }
	public IReadOnlyList<FmSynthRoutePoint> RoutePoints => _routePoints;
}

/// <summary>
/// Persistent authoring object for one FM-synthesized sound source. Semantic
/// sound behavior lives exclusively in <see cref="Graph"/>; node positions and
/// routing hints are editor metadata and may be changed without altering the
/// graph.
/// </summary>
public sealed class FmSynthDefinition : SongObject
{
	private FmSynthGraph _graph;

	public FmSynthDefinition(
		ObjectId id,
		string name,
		FmSynthGraph graph)
		: base(id, name)
	{
		_graph =
			graph
				?? throw new ArgumentNullException(nameof(graph));
	}

	public override SongObjectKind Kind => SongObjectKind.FmSynth;

	public FmSynthGraph Graph
	{
		get => _graph;
		set =>
			_graph =
				value
					?? throw new ArgumentNullException(nameof(value));
	}

	public List<FmSynthNodePosition> NodePositions { get; } = [];

	public List<FmSynthConnectionRoutingHint> ConnectionRoutingHints { get; } = [];
}
