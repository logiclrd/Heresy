using System;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Adapts the existing pattern-local keyboard/effect editors to a flattened
/// PatternEditorContext without teaching those editors about sequences. The
/// shared cursor temporarily exposes the underlying local pattern row while the
/// existing operation runs, then returns to its display-row coordinate.
/// </summary>
public static class PatternEditorContextCursor
{
	public static T EditCurrent<T>(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		Func<PatternEditorRow, T> operation)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(operation);

		if (context.Rows.Count == 0)
			throw new InvalidOperationException("The pattern editor context has no editable rows.");
		if ((uint)cursor.Row >= (uint)context.Rows.Count)
			throw new InvalidOperationException("The editor cursor lies outside the display-row context.");

		int displayRow = cursor.Row;
		PatternEditorRow row = context.GetRow(displayRow);
		if ((uint)cursor.Channel >= (uint)row.Pattern.ChannelCount)
		{
			throw new InvalidOperationException(
				"The editor cursor channel lies outside the underlying pattern row.");
		}

		int advanceGeneration = cursor.VerticalAdvanceGeneration;
		cursor.RemapRow(row.PatternRow);

		try
		{
			return operation(row);
		}
		finally
		{
			bool advanced =
				cursor.VerticalAdvanceGeneration != advanceGeneration;
			int restoredDisplayRow =
				advanced
					? Math.Min(context.Rows.Count - 1, displayRow + 1)
					: displayRow;
			cursor.RemapRow(restoredDisplayRow);
			PatternEditorRow restored = context.GetRow(restoredDisplayRow);
			cursor.ClampChannelPreservingField(
				restored.Pattern.ChannelCount);
		}
	}

	public static void MoveLeft(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> EditCurrent(
			context,
			cursor,
			row =>
			{
				cursor.MoveLeft(row.Pattern);
				return true;
			});

	public static void MoveRight(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> EditCurrent(
			context,
			cursor,
			row =>
			{
				cursor.MoveRight(row.Pattern);
				return true;
			});

	public static void MoveHome(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> EditCurrent(
			context,
			cursor,
			row =>
			{
				cursor.MoveHome(row.Pattern);
				return true;
			});

	public static void MoveEnd(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> EditCurrent(
			context,
			cursor,
			row =>
			{
				cursor.MoveEnd(row.Pattern);
				return true;
			});

	public static void MoveChannel(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		int delta)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (context.Rows.Count == 0)
			return;

		PatternEditorRow row =
			context.GetRow(cursor.Row);
		cursor.MoveChannel(
			row.Pattern.ChannelCount,
			delta);
	}

	public static void MoveUp(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> MoveVertical(context, cursor, delta: -1);

	public static void MoveDown(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> MoveVertical(context, cursor, delta: 1);

	private static void MoveVertical(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		int delta)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (context.Rows.Count == 0)
			return;

		if (delta < 0)
			cursor.MoveUp(context.Rows.Count);
		else if (delta > 0)
			cursor.MoveDown(context.Rows.Count);
		else
			return;

		PatternEditorRow row = context.GetRow(cursor.Row);
		cursor.ClampChannelPreservingField(row.Pattern.ChannelCount);
	}
}
