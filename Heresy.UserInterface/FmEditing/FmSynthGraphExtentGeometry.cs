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


/// <summary>
/// The bounded scrollbar model. Extra room is 20% of the *visible viewport*
/// on each side of the 20-DIP node/wire bounds. The actual camera position
/// is deliberately NOT a property of this range and may lie outside it.
/// </summary>
public readonly record struct FmSynthGraphScrollRange(
    double Left, double Top, double Right, double Bottom,
    double ViewportWidth, double ViewportHeight)
{
    public const double OverscanFraction = 0.2;

    public double Width => Math.Max(ViewportWidth, Right - Left);
    public double Height => Math.Max(ViewportHeight, Bottom - Top);
    public double MaximumOffsetX => Math.Max(0, Width - ViewportWidth);
    public double MaximumOffsetY => Math.Max(0, Height - ViewportHeight);

    public static FmSynthGraphScrollRange FromBounds(
        FmSynthGraphBounds bounds, double viewportWidth, double viewportHeight)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth < 0
            || !double.IsFinite(viewportHeight) || viewportHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        return new FmSynthGraphScrollRange(
            bounds.Left - OverscanFraction * viewportWidth,
            bounds.Top - OverscanFraction * viewportHeight,
            bounds.Right + OverscanFraction * viewportWidth,
            bounds.Bottom + OverscanFraction * viewportHeight,
            viewportWidth, viewportHeight);
    }

    /// <summary>Project an unrestricted camera onto the physical scrollbar
    /// tracks. Projection never changes the input camera.</summary>
    public FmSynthRoutePoint Project(FmSynthRoutePoint camera)
        => new(
            Math.Clamp(camera.X - Left, 0, MaximumOffsetX),
            Math.Clamp(camera.Y - Top, 0, MaximumOffsetY));

    /// <summary>A user-originated scrollbar movement adopts its bounded
    /// position as the new actual camera; no hidden overscroll is retained.</summary>
    public FmSynthRoutePoint CameraAt(FmSynthRoutePoint scrollOffset)
        => new(Left + Math.Clamp(scrollOffset.X, 0, MaximumOffsetX),
               Top + Math.Clamp(scrollOffset.Y, 0, MaximumOffsetY));
}
