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

	public static FmSynthConnectionPort[] GetPorts(
		FmSynthNode node,
		FmSynthLayoutRect bounds)
	{
		ArgumentNullException.ThrowIfNull(node);

		int inputCount = node switch
		{
			FmOscillatorNode => 1,
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
						bounds.CenterY)),
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
						bounds.Top
							+ (bounds.Height * (inputIndex + 1) / (inputCount + 1)))));
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
