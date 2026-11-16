using System;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// Defines the pixel-accurate proximity region for Synth-node connection
/// handles. A point on either side of a body edge can reveal the ports;
/// only actual hits on the node body can move that node.
/// </summary>
public static class FmSynthNodeHandleGeometry
{
    public const double ProximityPixels = 8.0;

    /// <summary>
    /// Minimum DIP distance to the rectangle's perimeter. Outside corner
    /// distances are Euclidean, not the minimum distance to infinite edge
    /// lines (which would falsely include remote diagonal points).
    /// </summary>
    public static double DistanceToBoundary(
        FmSynthLayoutRect rect, FmSynthRoutePoint point)
    {
        double outsideX = Math.Max(Math.Max(rect.Left - point.X, 0.0),
            point.X - rect.Right);
        double outsideY = Math.Max(Math.Max(rect.Top - point.Y, 0.0),
            point.Y - rect.Bottom);
        if (outsideX > 0.0 || outsideY > 0.0)
            return Math.Sqrt(outsideX * outsideX + outsideY * outsideY);

        return Math.Min(Math.Min(point.X - rect.Left, rect.Right - point.X),
            Math.Min(point.Y - rect.Top, rect.Bottom - point.Y));
    }

    /// <summary>
    /// Eight *physical* pixels, accounting for top-level render scaling;
    /// unlike node dragging, this region may extend outside the node body.
    /// </summary>
    public static bool IsNearBoundary(
        FmSynthLayoutRect rect, FmSynthRoutePoint point,
        double renderScaling)
    {
        if (!double.IsFinite(renderScaling) || renderScaling <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(renderScaling));
        return DistanceToBoundary(rect, point) * renderScaling
            <= ProximityPixels;
    }
}
