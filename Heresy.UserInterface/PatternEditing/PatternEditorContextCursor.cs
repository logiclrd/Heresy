using System;

using Heresy.Core.Patterns;

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
			// A note entry advances by the chosen tracker skip (0–9).
			// Mapping it as a boolean used to discard all but the first
			// step when the editor presents a flattened sequence.
			int steps = cursor.VerticalAdvanceGeneration - advanceGeneration;
			int restoredDisplayRow = displayRow;
			for (int step = 0; step < steps
				&& restoredDisplayRow < context.Rows.Count - 1; step++)
			{
				restoredDisplayRow++;
				// Repeated Down presses clamp the channel at EVERY row,
				// not only at the destination. A narrow intermediate
				// pattern can permanently reduce the selected channel.
				cursor.ClampChannelPreservingField(
					context.GetRow(restoredDisplayRow)
						.Pattern.ChannelCount);
			}
			cursor.RemapRow(restoredDisplayRow);
			cursor.ClampChannelPreservingField(
				context.GetRow(restoredDisplayRow).Pattern.ChannelCount);
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

	public static bool MoveToAdjacentNote(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		int delta)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (delta is not -1 and not 1)
			throw new ArgumentOutOfRangeException(nameof(delta));
		if (context.Rows.Count == 0)
			return false;

		PatternEditorRow row =
			context.GetRow(cursor.Row);

		int targetChannel;
		if (delta > 0)
		{
			targetChannel = cursor.Channel + 1;
			if (targetChannel >= row.Pattern.ChannelCount)
				return false;
		}
		else if (cursor.Field != PatternCellField.Note)
		{
			targetChannel = cursor.Channel;
		}
		else
		{
			targetChannel = cursor.Channel - 1;
			if (targetChannel < 0)
				return false;
		}

		cursor.SetPosition(
			cursor.Row,
			targetChannel,
			PatternCellField.Note);
		return true;
	}

	public static bool MoveToFirstRow(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> MoveToPatternBoundaryRow(
			context,
			cursor,
			first: true);

	public static bool MoveToLastRow(
		PatternEditorContext context,
		PatternEffectCursor cursor)
		=> MoveToPatternBoundaryRow(
			context,
			cursor,
			first: false);

	public static void MoveToTopLeft(
		PatternEditorContext context,
		PatternEffectCursor cursor)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (context.Rows.Count == 0)
			return;

		(int firstDisplayRow, _) =
			context.GetPatternDisplayBounds(cursor.Row);
		bool alreadyLocalTarget =
			cursor.Row == firstDisplayRow
				&& cursor.Channel == 0
				&& cursor.Field == PatternCellField.Note;

		cursor.SetPosition(
			alreadyLocalTarget ? 0 : firstDisplayRow,
			0,
			PatternCellField.Note);
	}

	public static void MoveToBottomRight(
		PatternEditorContext context,
		PatternEffectCursor cursor)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (context.Rows.Count == 0)
			return;

		(_, int lastDisplayRow) =
			context.GetPatternDisplayBounds(cursor.Row);
		PatternEditorRow localRow =
			context.GetRow(lastDisplayRow);
		int localChannel = localRow.Pattern.ChannelCount - 1;
		PatternCell? localCell =
			localRow.Pattern.Grid[
				localRow.PatternRow,
				localChannel];
		PatternCellField localField =
			PatternEffectCursor.GetLastKeyboardField(localCell);
		bool alreadyLocalTarget =
			cursor.Row == lastDisplayRow
				&& cursor.Channel == localChannel
				&& cursor.Field == localField;

		int targetDisplayRow =
			alreadyLocalTarget
				? context.Rows.Count - 1
				: lastDisplayRow;
		PatternEditorRow targetRow =
			context.GetRow(targetDisplayRow);
		int targetChannel =
			targetRow.Pattern.ChannelCount - 1;
		PatternCell? targetCell =
			targetRow.Pattern.Grid[
				targetRow.PatternRow,
				targetChannel];
		cursor.SetPosition(
			targetDisplayRow,
			targetChannel,
			PatternEffectCursor.GetLastKeyboardField(targetCell));
	}

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

	private static bool MoveToPatternBoundaryRow(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		bool first)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		if (context.Rows.Count == 0)
			return false;

		(int firstDisplayRow, int lastDisplayRow) =
			context.GetPatternDisplayBounds(cursor.Row);
		int localTarget =
			first
				? firstDisplayRow
				: lastDisplayRow;
		int target = localTarget;

		if (cursor.Row == localTarget)
		{
			int delta = first ? -1 : 1;
			int? adjacent =
				context.FindAdjacentPatternBoundaryDisplayRow(
					cursor.Row,
					delta,
					firstRow: first);
			if (adjacent is null)
				return false;
			target = adjacent.Value;
		}

		PatternEditorRow row =
			context.GetRow(target);
		int channel =
			Math.Min(
				cursor.Channel,
				row.Pattern.ChannelCount - 1);
		cursor.SetPosition(
			target,
			channel,
			cursor.Field);
		return true;
	}

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
