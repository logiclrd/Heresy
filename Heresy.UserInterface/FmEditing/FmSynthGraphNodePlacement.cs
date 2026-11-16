using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// Editor-only placement and viewport reveal for newly inserted Synth nodes.
/// The search uses the same complete, already-routed wire geometry as drawing
/// and scrolling; neither placement calculations nor revealing alter audio.
/// </summary>
public static class FmSynthGraphNodePlacement
{
    // The largest initial height of the eight nodes currently added from
    // the Synth toolbar (a one-input Operator or status+input node).
    // Dynamic later growth remains governed by the normal graph extents.
    public const double InitialNodeHeight =
        2 * (FmSynthConnectionPorts.FilterBorderThickness
            + FmSynthConnectionPorts.FilterVerticalMargin)
        + FmSynthConnectionPorts.FilterHeaderHeight
        + FmSynthConnectionPorts.StatusRowHeight
        + FmSynthConnectionPorts.FilterInputRowHeight;

    // Stable logical-space lattice: a camera movement changes which cells
    // are considered nearby, never the coordinates of the cells themselves.
    // A newly opened graph starts at (80,80), independently of its viewport.
    public const double GridOriginX = 80.0;
    public const double GridOriginY = 80.0;

    public static FmSynthRoutePoint FindUnobstructedPosition(
        IEnumerable<FmSynthLayoutRect> existingBodies,
        IEnumerable<IReadOnlyList<FmSynthRoutePoint>> renderedEdges,
        FmSynthRoutePoint camera,
        double viewportWidth,
        double viewportHeight,
        double nodeWidth,
        double nodeHeight)
    {
        ArgumentNullException.ThrowIfNull(existingBodies);
        ArgumentNullException.ThrowIfNull(renderedEdges);
        if (!double.IsFinite(nodeWidth) || nodeWidth <= 0
            || !double.IsFinite(nodeHeight) || nodeHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(nodeWidth));
        if (!double.IsFinite(viewportWidth) || viewportWidth < 0
            || !double.IsFinite(viewportHeight) || viewportHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));

        FmSynthLayoutRect[] bodies = existingBodies.ToArray();
        IReadOnlyList<FmSynthRoutePoint>[] edges = renderedEdges.ToArray();
        double margin = FmSynthGraphExtentGeometry.Margin;
        double stepX = nodeWidth + margin;
        double stepY = nodeHeight + margin;

        // Start at the first fixed lattice cell whose padded left/top edge
        // fits the camera. Auto-reveal and manual panning may move the camera
        // by arbitrary fractions of a cell; neither may shift the lattice.
        double firstX = GridOriginX + Math.Ceiling(
            (camera.X + margin - GridOriginX) / stepX) * stepX;
        double firstY = GridOriginY + Math.Ceiling(
            (camera.Y + margin - GridOriginY) / stepY) * stepY;

        // Prefer fully visible cells, and then expand right/down. Count
        // visible cells using the aligned first cell's real position rather
        // than assuming it lies exactly one margin inside the viewport.
        int visibleColumns = Math.Clamp(
            (int)Math.Floor(
                (camera.X + viewportWidth - margin - nodeWidth - firstX)
                / stepX) + 1, 1, 10);
        int visibleRows = Math.Clamp(
            (int)Math.Floor(
                (camera.Y + viewportHeight - margin - nodeHeight - firstY)
                / stepY) + 1, 1, 10);
        for (int band = 0; band < 12; band++)
        {
            int columns = visibleColumns + band;
            int rows = visibleRows + band;
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    if (band > 0 && row != rows - 1 && column != columns - 1)
                        continue;
                    FmSynthLayoutRect candidate = new(
                        firstX + column * stepX, firstY + row * stepY,
                        nodeWidth, nodeHeight);
                    if (IsUnobstructed(candidate, bodies, edges))
                        return new FmSynthRoutePoint(candidate.X, candidate.Y);
                }
            }
        }

        // A guaranteed escape for extremely dense graphs or distant routed
        // edges: place to the right of *all* bodies and rendered route points.
        FmSynthGraphBounds extent =
            FmSynthGraphExtentGeometry.Calculate(bodies, edges);
        // Even this rare escape remains on the *same* logical-space grid.
        double fallbackColumns = Math.Ceiling(
            Math.Max(0.0, (extent.Right + 1.0 - firstX) / stepX));
        return new FmSynthRoutePoint(
            firstX + fallbackColumns * stepX, firstY);
    }

    /// <summary>
    /// A complete 20-DIP buffer around the candidate must be clear of
    /// existing node bodies and *every segment* of every rendered edge.
    /// </summary>
    public static bool IsUnobstructed(
        FmSynthLayoutRect candidate,
        IEnumerable<FmSynthLayoutRect> existingBodies,
        IEnumerable<IReadOnlyList<FmSynthRoutePoint>> renderedEdges)
    {
        ArgumentNullException.ThrowIfNull(existingBodies);
        ArgumentNullException.ThrowIfNull(renderedEdges);
        FmSynthLayoutRect padded =
            candidate.Inflate(FmSynthGraphExtentGeometry.Margin);
        foreach (FmSynthLayoutRect body in existingBodies)
        {
            if (padded.Left < body.Right && padded.Right > body.Left
                && padded.Top < body.Bottom && padded.Bottom > body.Top)
                return false;
        }
        foreach (IReadOnlyList<FmSynthRoutePoint> edge in renderedEdges)
        {
            if (edge.Count == 1 && PointInRect(edge[0], padded))
                return false;
            for (int i = 1; i < edge.Count; i++)
            {
                if (SegmentTouchesRect(edge[i - 1], edge[i], padded))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// An insertion may intentionally reposition the camera to reveal the
    /// node and its 20-DIP buffer; unlike range recomputation, it is a
    /// user-action-driven camera change. If an axis is too narrow to fit the
    /// padded node, align its near edge rather than oscillating on layout.
    /// </summary>
    public static FmSynthRoutePoint CameraToReveal(
        FmSynthRoutePoint camera,
        FmSynthLayoutRect node,
        double viewportWidth,
        double viewportHeight)
    {
        double margin = FmSynthGraphExtentGeometry.Margin;
        static double Axis(double current, double viewport,
            double near, double far)
        {
            if (!double.IsFinite(viewport) || viewport <= 0)
                return current;
            if (far - near > viewport || near < current)
                return near;
            if (far > current + viewport)
                return far - viewport;
            return current;
        }
        return new FmSynthRoutePoint(
            Axis(camera.X, viewportWidth, node.Left - margin, node.Right + margin),
            Axis(camera.Y, viewportHeight, node.Top - margin, node.Bottom + margin));
    }

    private static bool PointInRect(
        FmSynthRoutePoint point, FmSynthLayoutRect rect)
        => point.X >= rect.Left && point.X <= rect.Right
            && point.Y >= rect.Top && point.Y <= rect.Bottom;

    // Liang-Barsky clipping: covers horizontal/vertical detours, diagonal
    // future routes, point segments and segments crossing without a vertex
    // within the candidate rectangle.
    private static bool SegmentTouchesRect(
        FmSynthRoutePoint start, FmSynthRoutePoint end,
        FmSynthLayoutRect rect)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double min = 0.0;
        double max = 1.0;
        bool Clip(double p, double q)
        {
            if (p == 0.0)
                return q >= 0.0;
            double ratio = q / p;
            if (p < 0.0)
            {
                if (ratio > max)
                    return false;
                min = Math.Max(min, ratio);
            }
            else
            {
                if (ratio < min)
                    return false;
                max = Math.Min(max, ratio);
            }
            return true;
        }

        return Clip(-dx, start.X - rect.Left)
            && Clip(dx, rect.Right - start.X)
            && Clip(-dy, start.Y - rect.Top)
            && Clip(dy, rect.Bottom - start.Y);
    }
}
