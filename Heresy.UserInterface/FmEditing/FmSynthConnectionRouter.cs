using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

public readonly record struct FmSynthLayoutRect(
	double X,
	double Y,
	double Width,
	double Height)
{
	public double Left => X;
	public double Right => X + Width;
	public double Top => Y;
	public double Bottom => Y + Height;
	public double CenterX => X + (Width / 2.0);
	public double CenterY => Y + (Height / 2.0);

	public FmSynthLayoutRect Inflate(double amount)
		=> new(
			X - amount,
			Y - amount,
			Width + (amount * 2.0),
			Height + (amount * 2.0));
}

/// <summary>
/// Deterministic editor-only orthogonal routing. Explicit routing hints are
/// treated as waypoints. Without hints, the router tries a direct dogleg first
/// and then above/below lanes around blocking node rectangles.
/// </summary>
public static class FmSynthConnectionRouter
{
	private const double Clearance = 16.0;
	private const double Lead = 24.0;

	public static FmSynthRoutePoint[] Route(
		FmSynthLayoutRect source,
		FmSynthLayoutRect target,
		IReadOnlyList<FmSynthLayoutRect> obstacles,
		IReadOnlyList<FmSynthRoutePoint>? routingHints)
	{
		ArgumentNullException.ThrowIfNull(obstacles);

		bool forward =
			source.CenterX <= target.CenterX;
		double direction =
			forward ? 1.0 : -1.0;
		FmSynthRoutePoint start =
			new(
				forward ? source.Right : source.Left,
				source.CenterY);
		FmSynthRoutePoint end =
			new(
				forward ? target.Left : target.Right,
				target.CenterY);

		if (routingHints is not null
			&& routingHints.Count != 0)
		{
			List<FmSynthRoutePoint> hinted = [start];
			foreach (FmSynthRoutePoint hint in routingHints)
				AppendOrthogonal(hinted, hint);
			AppendOrthogonal(hinted, end);
			return Simplify(hinted);
		}

		FmSynthLayoutRect[] blocked =
			obstacles
				.Select(rectangle =>
					rectangle.Inflate(Clearance))
				.ToArray();

		double middleX =
			(start.X + end.X)
				/ 2.0;
		FmSynthRoutePoint[] dogleg =
			Simplify(
				[
					start,
					new FmSynthRoutePoint(
						middleX,
						start.Y),
					new FmSynthRoutePoint(
						middleX,
						end.Y),
					end,
				]);
		if (PathClear(
			dogleg,
			blocked))
		{
			return dogleg;
		}

		double minimumTop =
			Math.Min(
				source.Top,
				target.Top);
		double maximumBottom =
			Math.Max(
				source.Bottom,
				target.Bottom);
		foreach (FmSynthLayoutRect obstacle in blocked)
		{
			minimumTop =
				Math.Min(
					minimumTop,
					obstacle.Top);
			maximumBottom =
				Math.Max(
					maximumBottom,
					obstacle.Bottom);
		}

		FmSynthRoutePoint[] above =
			LaneRoute(
				start,
				end,
				direction,
				minimumTop - Clearance);
		FmSynthRoutePoint[] below =
			LaneRoute(
				start,
				end,
				direction,
				maximumBottom + Clearance);

		bool aboveClear =
			PathClear(
				above,
				blocked);
		bool belowClear =
			PathClear(
				below,
				blocked);
		if (aboveClear && belowClear)
		{
			return PathLength(above)
				<= PathLength(below)
					? above
					: below;
		}
		if (aboveClear)
			return above;
		if (belowClear)
			return below;

		return dogleg;
	}

	public static bool CrossesInterior(
		IReadOnlyList<FmSynthRoutePoint> route,
		FmSynthLayoutRect rectangle)
	{
		ArgumentNullException.ThrowIfNull(route);
		for (int index = 1; index < route.Count; index++)
		{
			if (SegmentCrossesInterior(
				route[index - 1],
				route[index],
				rectangle))
			{
				return true;
			}
		}
		return false;
	}

	private static FmSynthRoutePoint[] LaneRoute(
		FmSynthRoutePoint start,
		FmSynthRoutePoint end,
		double direction,
		double laneY)
		=> Simplify(
			[
				start,
				new FmSynthRoutePoint(
					start.X + (direction * Lead),
					start.Y),
				new FmSynthRoutePoint(
					start.X + (direction * Lead),
					laneY),
				new FmSynthRoutePoint(
					end.X - (direction * Lead),
					laneY),
				new FmSynthRoutePoint(
					end.X - (direction * Lead),
					end.Y),
				end,
			]);

	private static void AppendOrthogonal(
		List<FmSynthRoutePoint> route,
		FmSynthRoutePoint target)
	{
		FmSynthRoutePoint current =
			route[^1];
		if (current.X != target.X
			&& current.Y != target.Y)
		{
			route.Add(
				new FmSynthRoutePoint(
					target.X,
					current.Y));
		}
		if (route[^1] != target)
			route.Add(target);
	}

	private static bool PathClear(
		IReadOnlyList<FmSynthRoutePoint> route,
		IReadOnlyList<FmSynthLayoutRect> obstacles)
	{
		foreach (FmSynthLayoutRect obstacle in obstacles)
		{
			if (CrossesInterior(
				route,
				obstacle))
			{
				return false;
			}
		}
		return true;
	}

	private static bool SegmentCrossesInterior(
		FmSynthRoutePoint first,
		FmSynthRoutePoint second,
		FmSynthLayoutRect rectangle)
	{
		if (first.Y == second.Y)
		{
			double y = first.Y;
			if (!(y > rectangle.Top
				&& y < rectangle.Bottom))
			{
				return false;
			}

			double minimum =
				Math.Min(
					first.X,
					second.X);
			double maximum =
				Math.Max(
					first.X,
					second.X);
			return maximum > rectangle.Left
				&& minimum < rectangle.Right;
		}

		if (first.X == second.X)
		{
			double x = first.X;
			if (!(x > rectangle.Left
				&& x < rectangle.Right))
			{
				return false;
			}

			double minimum =
				Math.Min(
					first.Y,
					second.Y);
			double maximum =
				Math.Max(
					first.Y,
					second.Y);
			return maximum > rectangle.Top
				&& minimum < rectangle.Bottom;
		}

		return true;
	}

	private static double PathLength(
		IReadOnlyList<FmSynthRoutePoint> route)
	{
		double result = 0.0;
		for (int index = 1; index < route.Count; index++)
		{
			result +=
				Math.Abs(
					route[index].X
						- route[index - 1].X)
				+ Math.Abs(
					route[index].Y
						- route[index - 1].Y);
		}
		return result;
	}

	private static FmSynthRoutePoint[] Simplify(
		IReadOnlyList<FmSynthRoutePoint> route)
	{
		List<FmSynthRoutePoint> result = [];
		foreach (FmSynthRoutePoint point in route)
		{
			if (result.Count != 0
				&& result[^1] == point)
			{
				continue;
			}

			result.Add(point);
			while (result.Count >= 3)
			{
				FmSynthRoutePoint first =
					result[^3];
				FmSynthRoutePoint middle =
					result[^2];
				FmSynthRoutePoint last =
					result[^1];
				if ((first.X == middle.X
						&& middle.X == last.X)
					|| (first.Y == middle.Y
						&& middle.Y == last.Y))
				{
					result.RemoveAt(
						result.Count - 2);
				}
				else
				{
					break;
				}
			}
		}
		return [.. result];
	}
}
