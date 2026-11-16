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
	public const double BaseNodeHeight = 72.0;

	// Shared DIP measurements for the Filter and Operator UI and their
	// framework-neutral connection geometry. Labels occupy one actual row
	// each, rather than dividing the node rectangle into N equal slices.
	public const double FilterBorderThickness = 2.0;
	public const double FilterVerticalMargin = 6.0;
	public const double FilterHeaderHeight = 20.0;
	public const double FilterInputRowHeight = 22.0;
	public const double OperatorModeRowHeight = 22.0;
	public const double StatusRowHeight = 22.0;
	// Halfway between the original centered text and the 6-DIP drop.
	// This adjusts only the "Out" text; output ports remain fixed at
	// 36 DIPs from the top, regardless of the full node height.
	public const double OutputLabelTextDrop = 3.0;

	// Every output remains fixed at the vertical center of the base node,
	// even when Filter/Operator grows taller.
	public static double OutputCenterOffset => BaseNodeHeight / 2.0;

	public static double FilterInputCenterOffset(int inputIndex)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(inputIndex);
		return FilterBorderThickness + FilterVerticalMargin + FilterHeaderHeight
			+ FilterInputRowHeight * (inputIndex + 0.5);
	}

	// Oscillator, Source and Multi show their original status beneath
	// the header, followed by one distinctly labelled blue input row.
	public static double SingleInputCenterOffset =>
		FilterBorderThickness + FilterVerticalMargin + FilterHeaderHeight
			+ StatusRowHeight + FilterInputRowHeight / 2.0;

	public static double OperatorInputCenterOffset(int inputIndex)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(inputIndex);
		return FilterBorderThickness + FilterVerticalMargin + FilterHeaderHeight
			+ OperatorModeRowHeight
			+ FilterInputRowHeight * (inputIndex + 0.5);
	}

	public static double GetNodeHeight(FmSynthNode node) => node switch
	{
		FmFilterNode filter => Math.Max(BaseNodeHeight,
			2.0 * (FilterBorderThickness + FilterVerticalMargin)
			+ FilterHeaderHeight
			+ FilterInputRowHeight * filter.GetPorts().Count),
		FmOperatorNode operation => Math.Max(BaseNodeHeight,
			2.0 * (FilterBorderThickness + FilterVerticalMargin)
			+ FilterHeaderHeight + OperatorModeRowHeight
			+ FilterInputRowHeight * operation.InputNodeIds.Count),
		FmOscillatorNode or FmSoundSourceNode or FmMultiNode =>
			Math.Max(BaseNodeHeight,
				2.0 * (FilterBorderThickness + FilterVerticalMargin)
				+ FilterHeaderHeight + StatusRowHeight + FilterInputRowHeight),
		_ => BaseNodeHeight,
	};

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
						bounds.Top + OutputCenterOffset)),
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
						bounds.Top + (node switch
						{
							FmFilterNode => FilterInputCenterOffset(inputIndex),
							FmOperatorNode operation
								when inputIndex == operation.InputNodeIds.Count =>
									bounds.Height, // Gold append handle: bottom-left corner.
							FmOperatorNode => OperatorInputCenterOffset(inputIndex),
							FmOscillatorNode or FmSoundSourceNode or FmMultiNode =>
								SingleInputCenterOffset,
							_ => bounds.Height * (inputIndex + 1) / (inputCount + 1),
						}))));
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
