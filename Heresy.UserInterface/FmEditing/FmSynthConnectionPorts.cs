using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

public enum FmSynthConnectionPortKind
{
	Output,
	Input,
}

/// <summary>
/// A semantic output/input port and its position on a node border.
/// The last operator input port appends a new ordered input; the other
/// operator input ports replace the corresponding existing connection.
/// </summary>
public readonly record struct FmSynthConnectionPort(
	int NodeId,
	FmSynthConnectionPortKind Kind,
	int InputIndex,
	FmSynthRoutePoint Center);

public readonly record struct FmSynthConnectionCandidate(
	int SourceNodeId,
	int TargetNodeId,
	int TargetInputIndex);

/// <summary>
/// Framework-neutral port layout, hit testing, and drag-direction resolution.
/// Semantic graph validation belongs to FmSynthDocumentEditor.
/// </summary>
public static class FmSynthConnectionPorts
{
	public const double HitRadius = 9.0;

	// The Filter's visual header and input rows share these measurements
	// with the geometric ports. A text metric or arbitrary equal division
	// of the whole node height cannot define a labelled socket position.
	public const double FilterBorderThickness = 2.0;
	public const double FilterVerticalMargin = 6.0;
	public const double FilterHeaderHeight = 20.0;
	public const double FilterInputRowHeight = 22.0;

	public static double FilterHeaderCenterOffset =>
		FilterBorderThickness + FilterVerticalMargin
			+ FilterHeaderHeight / 2.0;

	public static double FilterInputCenterOffset(int inputIndex)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(inputIndex);
		return FilterBorderThickness + FilterVerticalMargin + FilterHeaderHeight
			+ FilterInputRowHeight * (inputIndex + 0.5);
	}

	public static double GetNodeHeight(FmSynthNode node) =>
		node is FmFilterNode filter
			? Math.Max(72.0,
				2.0 * (FilterBorderThickness + FilterVerticalMargin)
				+ FilterHeaderHeight
				+ FilterInputRowHeight * filter.GetPorts().Count)
			: 72.0;

	/// <summary>Actual wired inputs with the correct stable port ordinal.
	/// A Filter's disconnected and invalidated parameter ports cannot
	/// shift another port into their slot.</summary>
	public static IReadOnlyList<(int Index, int SourceId)> GetConnectedInputs(
		FmSynthNode node)
	{
		if (node is FmFilterNode filter)
			return filter.GetPorts()
				.Where(port => port.SourceNodeId.HasValue)
				.Select(port => (port.Index, port.SourceNodeId!.Value))
				.ToArray();
		return node.InputNodeIds.Select((id, index) => (index, id)).ToArray();
	}

	public static FmSynthConnectionPort[] GetPorts(
		FmSynthNode node,
		FmSynthLayoutRect bounds)
	{
		ArgumentNullException.ThrowIfNull(node);

		int inputCount = node switch
		{
			FmOscillatorNode => 1,
			FmSoundSourceNode => 1,
			FmMultiNode => 1,
			FmFilterNode filter => filter.GetPorts().Count,
			FmOperatorNode operation => operation.InputNodeIds.Count + 1,
			_ => 0,
		};

		List<FmSynthConnectionPort> ports =
			[
				new FmSynthConnectionPort(
					node.Id,
					FmSynthConnectionPortKind.Output,
					-1,
					new FmSynthRoutePoint(
						bounds.Right,
						bounds.Top + (node is FmFilterNode
							? FilterHeaderCenterOffset : bounds.Height / 2.0))),
			];
		for (int inputIndex = 0; inputIndex < inputCount; inputIndex++)
		{
			ports.Add(
				new FmSynthConnectionPort(
					node.Id,
					FmSynthConnectionPortKind.Input,
					inputIndex,
					new FmSynthRoutePoint(
						bounds.Left,
						bounds.Top + (node is FmFilterNode
							? FilterInputCenterOffset(inputIndex)
							: bounds.Height * (inputIndex + 1) / (inputCount + 1)))));
		}
		return [.. ports];
	}

	public static FmSynthConnectionPort? HitTest(
		IEnumerable<FmSynthConnectionPort> ports,
		FmSynthRoutePoint pointer)
	{
		ArgumentNullException.ThrowIfNull(ports);

		FmSynthConnectionPort? nearest = null;
		double bestSquared = HitRadius * HitRadius;
		foreach (FmSynthConnectionPort port in ports)
		{
			double dx = pointer.X - port.Center.X;
			double dy = pointer.Y - port.Center.Y;
			double distanceSquared = dx * dx + dy * dy;
			if (distanceSquared <= bestSquared)
			{
				bestSquared = distanceSquared;
				nearest = port;
			}
		}
		return nearest;
	}

	public static bool TryResolve(
		FmSynthConnectionPort first,
		FmSynthConnectionPort second,
		out FmSynthConnectionCandidate candidate)
	{
		candidate = default;
		if (first.NodeId == second.NodeId
			|| first.Kind == second.Kind)
		{
			return false;
		}

		FmSynthConnectionPort output =
			first.Kind == FmSynthConnectionPortKind.Output
				? first
				: second;
		FmSynthConnectionPort input =
			first.Kind == FmSynthConnectionPortKind.Input
				? first
				: second;
		if (input.InputIndex < 0)
			return false;

		candidate =
			new FmSynthConnectionCandidate(
				output.NodeId,
				input.NodeId,
				input.InputIndex);
		return true;
	}
}
