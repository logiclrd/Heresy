using System;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Time-based vertical edge scrolling for pointer-captured song-tree drags.
/// The cursor must pass beyond the visible viewport before scrolling begins.
/// Speed is proportional to the overshoot, capped for control at large distances.
/// All dimensions and offsets use Avalonia device-independent pixels.
/// </summary>
public static class SongTreeDragAutoScroll
{
    public const double SpeedPerOutsideDip = 8.0;
    public const double MaximumSpeedDipsPerSecond = 1600.0;

    public static double Velocity(double pointerY, double viewportTop, double viewportHeight)
    {
        if (!double.IsFinite(pointerY)
            || !double.IsFinite(viewportTop)
            || !double.IsFinite(viewportHeight)
            || viewportHeight <= 0)
        {
            return 0;
        }

        double outside = pointerY < viewportTop
            ? pointerY - viewportTop
            : pointerY > viewportTop + viewportHeight
                ? pointerY - (viewportTop + viewportHeight)
                : 0;

        return Math.Clamp(outside * SpeedPerOutsideDip,
            -MaximumSpeedDipsPerSecond, MaximumSpeedDipsPerSecond);
    }

    public static double AdvanceOffset(
        double offset,
        double extentHeight,
        double viewportHeight,
        double velocityDipsPerSecond,
        double elapsedSeconds)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(extentHeight)
            || !double.IsFinite(viewportHeight) || !double.IsFinite(velocityDipsPerSecond)
            || !double.IsFinite(elapsedSeconds))
        {
            return offset;
        }

        double maximum = Math.Max(0, extentHeight - viewportHeight);
        double elapsed = Math.Clamp(elapsedSeconds, 0, 0.1);
        return Math.Clamp(offset + velocityDipsPerSecond * elapsed, 0, maximum);
    }
}
