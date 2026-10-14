using System;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Page keys are repeated tracker cursor steps, not independent ScrollViewer
/// paging. Row height includes the shared top grid-line border, so the
/// number of complete rows in the actual grid viewport is floor(H / h).
/// </summary>
public static class PatternPageNavigation
{
	public static int RowsPerPage(double viewportHeight, double rowHeight)
	{
		if (!(rowHeight > 0) || !double.IsFinite(rowHeight))
			throw new ArgumentOutOfRangeException(nameof(rowHeight));
		if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
			return 0;

		return (int)Math.Min(int.MaxValue,
			Math.Floor(viewportHeight / rowHeight));
	}

	/// <summary>
	/// Navigate as though the user pressed Up/Down `RowsPerPage` times,
	/// including intermediate Pattern channel clamping and effect collapse.
	/// Avoid pointless repeated clamped steps after the context boundary.
	/// </summary>
	public static int Move(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		double viewportHeight,
		double rowHeight,
		bool down)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		int pageRows = RowsPerPage(viewportHeight, rowHeight);
		if (pageRows == 0 || context.Rows.Count == 0)
			return pageRows;

		// One at-boundary step still performs the same cursor housekeeping
		// (collapse expanded effects / clear an uncommitted parameter nibble)
		// as pressing the corresponding arrow key at that boundary.
		int available = down
			? context.Rows.Count - 1 - cursor.Row
			: cursor.Row;
		int steps = Math.Min(pageRows, Math.Max(1, available));
		for (int i = 0; i < steps; i++)
		{
			if (down)
				PatternEditorContextCursor.MoveDown(context, cursor);
			else
				PatternEditorContextCursor.MoveUp(context, cursor);
		}
		return pageRows;
	}

	/// <summary>
	/// Scroll only enough to show the target cell. Keep an already visible
	/// cell and its scroll offset entirely unchanged, including horizontal
	/// scroll handled by the caller.
	/// </summary>
	public static double OffsetToKeepVisible(
		double cellTop,
		double cellHeight,
		double currentOffset,
		double viewportHeight,
		double extentHeight)
	{
		if (!double.IsFinite(cellTop)
			|| !double.IsFinite(cellHeight)
			|| !(cellHeight > 0)
			|| !double.IsFinite(currentOffset)
			|| !(viewportHeight > 0)
			|| !double.IsFinite(viewportHeight)
			|| !double.IsFinite(extentHeight))
		{
			return currentOffset;
		}

		double requested = currentOffset;
		if (cellTop < currentOffset)
			requested = cellTop;
		else if (cellTop + cellHeight > currentOffset + viewportHeight)
		{
			requested = cellHeight > viewportHeight
				? cellTop
				: cellTop + cellHeight - viewportHeight;
		}

		return Math.Clamp(requested, 0,
			Math.Max(0, extentHeight - viewportHeight));
	}
}
