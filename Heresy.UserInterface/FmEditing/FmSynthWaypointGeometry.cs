using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// One graph wire as it actually appears on screen. Connection identities
/// include the target slot because operators may use one source repeatedly.
/// </summary>
public sealed record FmSynthRenderedConnection(
	FmSynthConnectionCandidate Connection,
	IReadOnlyList<FmSynthRoutePoint> Route,
	IReadOnlyList<FmSynthRoutePoint> Waypoints);

public readonly record struct FmSynthConnectionLineHit(
	FmSynthConnectionCandidate Connection,
	int SegmentIndex,
	int InsertIndex,
	FmSynthRoutePoint NearestPoint);

public readonly record struct FmSynthWaypointHit(
	FmSynthConnectionCandidate Connection,
	int WaypointIndex);

/// <summary>
/// Framework-neutral graph hit testing and persistent waypoint ordering.
/// Coordinates are Avalonia DIPs. Wire hits include the visible pen's
/// half-width PLUS a buffer measured in DIPs; waypoint hits are circular
/// and extend three DIPs outside the rendered waypoint's outlined edge.
/// </summary>
public static class FmSynthWaypointGeometry
{
	/// <summary>Additional DIP clearance outside either edge of a
	/// visible wire stroke (not the distance from its centerline).
	/// This is four physical pixels at 100% scaling, six at 150%.</summary>
	public const double WireBufferDips = 4.0;
	public const double WireStrokeWidthDips = 2.0;
	public const double SelectedWireStrokeWidthDips = 3.0;
	/// <summary>Radius of the visible gold waypoint disc in DIPs;
	/// shared with the canvas renderer to prevent hit/paint divergence.</summary>
	public const double WaypointRadiusDips = 4.5;
	/// <summary>Width of the black outline, centered on the disc's radius.</summary>
	public const double WaypointOutlineWidthDips = 1.5;
	/// <summary>Additional reach beyond the rendered outline's outer edge.</summary>
	public const double WaypointBufferDips = 3.0;
	/// <summary>Complete waypoint hit radius, in DIPs:
	/// 4.5 + 1.5/2 + 3 = 8.25 DIPs from the waypoint center.</summary>
	public const double WaypointHitRadiusDips =
		WaypointRadiusDips + WaypointOutlineWidthDips / 2.0
		+ WaypointBufferDips;

	public static FmSynthConnectionLineHit? HitConnection(
		IEnumerable<FmSynthRenderedConnection> connections,
		FmSynthRoutePoint pointer,
		double renderScaling,
		FmSynthConnectionCandidate? selectedConnection = null)
	{
		ArgumentNullException.ThrowIfNull(connections);
		ValidateScaling(renderScaling);

		// Both wire and waypoint radii use Avalonia DIPs, matching
		// their actual rendered geometry regardless of display scaling.
		// The centerline radius includes half the rendered stroke width
		// plus four DIPs on either side.
		FmSynthConnectionLineHit? best = null;
		double bestSquared = double.PositiveInfinity;
		foreach (FmSynthRenderedConnection connection in connections)
		{
			double penWidthDips = connection.Connection == selectedConnection
				? SelectedWireStrokeWidthDips : WireStrokeWidthDips;
			double maximumSquared = Math.Pow(
				penWidthDips / 2.0 + WireBufferDips, 2);
			for (int index = 0; index + 1 < connection.Route.Count; index++)
			{
				FmSynthRoutePoint start = connection.Route[index];
				FmSynthRoutePoint end = connection.Route[index + 1];
				double dx = end.X - start.X;
				double dy = end.Y - start.Y;
				double lengthSquared = dx * dx + dy * dy;
				if (lengthSquared == 0.0)
					continue;

				double progress =
					Math.Clamp(
						((pointer.X - start.X) * dx
							+ (pointer.Y - start.Y) * dy) / lengthSquared,
						0.0,
						1.0);
				FmSynthRoutePoint nearest = new(
					start.X + progress * dx,
					start.Y + progress * dy);
				double xx = pointer.X - nearest.X;
				double yy = pointer.Y - nearest.Y;
				double distanceSquared = xx * xx + yy * yy;
				if (distanceSquared > maximumSquared
					|| distanceSquared >= bestSquared)
				{
					continue;
				}

				bestSquared = distanceSquared;
				best = new FmSynthConnectionLineHit(
					connection.Connection,
					index,
					InsertionIndex(connection, index),
					nearest);
			}
		}
		return best;
	}

	public static FmSynthWaypointHit? HitWaypoint(
		IEnumerable<FmSynthRenderedConnection> connections,
		FmSynthRoutePoint pointer,
		double renderScaling)
	{
		ArgumentNullException.ThrowIfNull(connections);
		ValidateScaling(renderScaling);

		// Match the gold disc plus its centered outline, with exactly
		// three DIPs of extra pointer reach outside the visible edge.
		double bestSquared = WaypointHitRadiusDips * WaypointHitRadiusDips;
		FmSynthWaypointHit? best = null;
		foreach (FmSynthRenderedConnection connection in connections)
		{
			for (int index = 0; index < connection.Waypoints.Count; index++)
			{
				FmSynthRoutePoint waypoint = connection.Waypoints[index];
				double dx = waypoint.X - pointer.X;
				double dy = waypoint.Y - pointer.Y;
				double squared = dx * dx + dy * dy;
				if (squared <= bestSquared)
				{
					bestSquared = squared;
					best = new FmSynthWaypointHit(connection.Connection, index);
				}
			}
		}
		return best;
	}

	/// <summary>
	/// Existing hints appear on the orthogonal route in order. Count only
	/// hints reached by the segment's START vertex; this inserts a new hint
	/// before the next existing waypoint and after every preceding one.
	/// </summary>
	public static int InsertionIndex(
		FmSynthRenderedConnection connection,
		int segmentIndex)
	{
		ArgumentNullException.ThrowIfNull(connection);
		if (segmentIndex < 0 || segmentIndex + 1 >= connection.Route.Count)
			throw new ArgumentOutOfRangeException(nameof(segmentIndex));

		int count = 0;
		for (int vertex = 0; vertex <= segmentIndex; vertex++)
		{
			while (count < connection.Waypoints.Count
				&& connection.Route[vertex] == connection.Waypoints[count])
			{
				count++;
			}
		}
		return count;
	}

	public static FmSynthRoutePoint[] Insert(
		IReadOnlyList<FmSynthRoutePoint> waypoints,
		int index,
		FmSynthRoutePoint point)
	{
		ArgumentNullException.ThrowIfNull(waypoints);
		if (index < 0 || index > waypoints.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		return [
			.. waypoints.Take(index),
			point,
			.. waypoints.Skip(index),
		];
	}

	public static FmSynthRoutePoint[] Replace(
		IReadOnlyList<FmSynthRoutePoint> waypoints,
		int index,
		FmSynthRoutePoint point)
	{
		ArgumentNullException.ThrowIfNull(waypoints);
		if (index < 0 || index >= waypoints.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		FmSynthRoutePoint[] replacement = [.. waypoints];
		replacement[index] = point;
		return replacement;
	}

	public static FmSynthRoutePoint[] Remove(
		IReadOnlyList<FmSynthRoutePoint> waypoints,
		int index)
	{
		ArgumentNullException.ThrowIfNull(waypoints);
		if (index < 0 || index >= waypoints.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
		return [
			.. waypoints.Take(index),
			.. waypoints.Skip(index + 1),
		];
	}

	private static void ValidateScaling(double renderScaling)
	{
		if (!double.IsFinite(renderScaling) || renderScaling <= 0)
			throw new ArgumentOutOfRangeException(nameof(renderScaling));
	}
}
