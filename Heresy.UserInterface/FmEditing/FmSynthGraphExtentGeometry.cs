using System;
using System.Collections.Generic;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// The editor's full graph bounds in logical graph coordinates. Calculated
/// from node bodies and the ACTUAL rendered connection segments, including
/// outer routing lanes and explicit waypoints (not just their endpoints).
/// The 20-DIP margin is added independently on all four sides.
/// </summary>
public readonly record struct FmSynthGraphBounds(
    double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double OriginX => -Left;
    public double OriginY => -Top;
}

public static class FmSynthGraphExtentGeometry
{
    public const double Margin = 20.0;

    public static FmSynthGraphBounds Calculate(
        IEnumerable<FmSynthLayoutRect> nodeBodies,
        IEnumerable<IReadOnlyList<FmSynthRoutePoint>> routedEdges)
    {
        ArgumentNullException.ThrowIfNull(nodeBodies);
        ArgumentNullException.ThrowIfNull(routedEdges);

        double left = double.PositiveInfinity;
        double top = double.PositiveInfinity;
        double right = double.NegativeInfinity;
        double bottom = double.NegativeInfinity;

        void Add(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        foreach (FmSynthLayoutRect node in nodeBodies)
        {
            Add(node.Left, node.Top);
            Add(node.Right, node.Bottom);
        }

        foreach (IReadOnlyList<FmSynthRoutePoint> edge in routedEdges)
            foreach (FmSynthRoutePoint vertex in edge)
                Add(vertex.X, vertex.Y);

        if (double.IsPositiveInfinity(left))
            return new FmSynthGraphBounds(0, 0, 2 * Margin, 2 * Margin);

        return new FmSynthGraphBounds(
            left - Margin, top - Margin,
            right + Margin, bottom + Margin);
    }
}
