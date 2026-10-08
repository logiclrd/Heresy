using System;
using System.Collections.Generic;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// Arrowhead for a directed graph connection. Tip is at the consumer end;
/// wings extend backward along the final nonzero route segment.
/// </summary>
public readonly record struct FmSynthConnectionArrowhead(
	FmSynthRoutePoint Tip,
	FmSynthRoutePoint FirstWing,
	FmSynthRoutePoint SecondWing);

public static class FmSynthConnectionArrowheadGeometry
{
	private const double Length = 8.0;
	private const double HalfWidth = 4.0;

	public static FmSynthConnectionArrowhead? FromRoute(
		IReadOnlyList<FmSynthRoutePoint> route)
	{
		ArgumentNullException.ThrowIfNull(route);
		if (route.Count < 2)
			return null;

		FmSynthRoutePoint tip = route[^1];
		for (int index = route.Count - 2; index >= 0; index--)
		{
			double dx = tip.X - route[index].X;
			double dy = tip.Y - route[index].Y;
			double distance = Math.Sqrt((dx * dx) + (dy * dy));
			if (distance == 0.0)
				continue;

			// Keep the small arrow inside its final segment, even when
			// closely spaced nodes produce an unusually short connection.
			double length = Math.Min(Length, distance);
			double width = HalfWidth * (length / Length);
			double ux = dx / distance;
			double uy = dy / distance;
			double baseX = tip.X - (ux * length);
			double baseY = tip.Y - (uy * length);
			return new FmSynthConnectionArrowhead(
				tip,
				new FmSynthRoutePoint(
					baseX - (uy * width),
					baseY + (ux * width)),
				new FmSynthRoutePoint(
					baseX + (uy * width),
					baseY - (ux * width)));
		}

		return null;
	}
}
