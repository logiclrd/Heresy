using System;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// The only scrolling permitted when a tree drag/drop completes: preserve
/// the original viewport if the moved row fits, otherwise reveal that row
/// by the smallest possible change in vertical offset.
/// Positions are measured in document-content device-independent pixels.
/// </summary>
public static class SongTreeDropVisibility
{
    public static double MinimalOffset(
        double originalOffset,
        double viewportHeight,
        double contentHeight,
        double rowTop,
        double rowHeight)
    {
        if (!double.IsFinite(originalOffset)
            || !double.IsFinite(viewportHeight) || viewportHeight <= 0
            || !double.IsFinite(contentHeight) || contentHeight < 0
            || !double.IsFinite(rowTop)
            || !double.IsFinite(rowHeight) || rowHeight <= 0)
        {
            return originalOffset;
        }

        double maximum = Math.Max(0, contentHeight - viewportHeight);
        double offset = Math.Clamp(originalOffset, 0, maximum);
        double bottom = rowTop + rowHeight;

        // A row taller than the viewport cannot fit entirely. Align its
        // nearest edge, rather than oscillating between two impossible
        // visibility constraints.
        if (rowHeight > viewportHeight)
        {
            if (rowTop > offset)
                return Math.Clamp(rowTop, 0, maximum);
            if (bottom < offset + viewportHeight)
                return Math.Clamp(bottom - viewportHeight, 0, maximum);
            return offset;
        }

        if (rowTop < offset)
            return Math.Clamp(rowTop, 0, maximum);
        if (bottom > offset + viewportHeight)
            return Math.Clamp(bottom - viewportHeight, 0, maximum);

        return offset;
    }
}
