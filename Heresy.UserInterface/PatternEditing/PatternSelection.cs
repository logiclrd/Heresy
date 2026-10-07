using System;

using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public readonly record struct PatternSelectionPoint(
	int Row,
	int Channel);

public readonly record struct PatternSelectionRegion(
	int TopRow,
	int LeftChannel,
	int BottomRow,
	int RightChannel)
{
	public int RowCount => BottomRow - TopRow + 1;

	public bool Contains(
		int row,
		int channel)
		=> row >= TopRow
			&& row <= BottomRow
			&& channel >= LeftChannel
			&& channel <= RightChannel;
}

public sealed class PatternSelectionState
{
	public PatternSelectionPoint? Start { get; private set; }

	public PatternSelectionPoint? End { get; private set; }

	public PatternSelectionRegion? Region
	{
		get
		{
			if (Start is not PatternSelectionPoint start
				|| End is not PatternSelectionPoint end)
			{
				return null;
			}

			return new PatternSelectionRegion(
				Math.Min(start.Row, end.Row),
				Math.Min(start.Channel, end.Channel),
				Math.Max(start.Row, end.Row),
				Math.Max(start.Channel, end.Channel));
		}
	}

	public void SetStart(
		int row,
		int channel)
	{
		PatternSelectionPoint point =
			ValidatePoint(row, channel);
		Start = point;
		End ??= point;
	}

	public void SetEnd(
		int row,
		int channel)
	{
		PatternSelectionPoint point =
			ValidatePoint(row, channel);
		End = point;
		Start ??= point;
	}

	public void SetRegion(
		PatternSelectionRegion region)
	{
		if (region.TopRow < 0
			|| region.LeftChannel < 0
			|| region.BottomRow < region.TopRow
			|| region.RightChannel < region.LeftChannel)
		{
			throw new ArgumentOutOfRangeException(nameof(region));
		}

		Start =
			new PatternSelectionPoint(
				region.TopRow,
				region.LeftChannel);
		End =
			new PatternSelectionPoint(
				region.BottomRow,
				region.RightChannel);
	}

	public void Extend(
		int originalRow,
		int originalChannel,
		int newRow,
		int newChannel)
	{
		PatternSelectionPoint original =
			ValidatePoint(originalRow, originalChannel);
		PatternSelectionPoint next =
			ValidatePoint(newRow, newChannel);

		Start ??= original;
		End = next;
	}

	public void Clear()
	{
		Start = null;
		End = null;
	}

	private static PatternSelectionPoint ValidatePoint(
		int row,
		int channel)
	{
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		return new PatternSelectionPoint(row, channel);
	}
}

public enum PatternSelectionCommand
{
	SetStart,
	SetEnd,
	SelectMajorBlock,
	SelectColumnOrPattern,
	Clear,
	ExtendLeft,
	ExtendRight,
	ExtendUp,
	ExtendDown,
}

public static class PatternSelectionKeyboard
{
	public static bool TryGetCommand(
		Key key,
		KeyModifiers modifiers,
		out PatternSelectionCommand command)
	{
		if (modifiers == KeyModifiers.Alt)
		{
			command =
				key switch
				{
					Key.B => PatternSelectionCommand.SetStart,
					Key.E => PatternSelectionCommand.SetEnd,
					Key.D => PatternSelectionCommand.SelectMajorBlock,
					Key.L => PatternSelectionCommand.SelectColumnOrPattern,
					Key.U => PatternSelectionCommand.Clear,
					_ => default,
				};
			return key is Key.B or Key.E or Key.D or Key.L or Key.U;
		}

		if (modifiers == KeyModifiers.Shift)
		{
			command =
				key switch
				{
					Key.Left => PatternSelectionCommand.ExtendLeft,
					Key.Right => PatternSelectionCommand.ExtendRight,
					Key.Up => PatternSelectionCommand.ExtendUp,
					Key.Down => PatternSelectionCommand.ExtendDown,
					_ => default,
				};
			return key is Key.Left or Key.Right or Key.Up or Key.Down;
		}

		command = default;
		return false;
	}
}

public static class PatternSelectionEditor
{
	public static void SelectMajorBlock(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		PatternSelectionState selection)
	{
		Validate(context, cursor, selection);

		PatternEditorRow row =
			context.GetRow(cursor.Row);
		(int firstRow, int lastRow) =
			context.GetPatternDisplayBounds(cursor.Row);
		int majorRows =
			Math.Max(1, row.Pattern.MajorHighlightRows);

		PatternSelectionRegion? current =
			selection.Region;
		if (current is PatternSelectionRegion region
			&& region.Contains(cursor.Row, cursor.Channel)
			&& region.TopRow >= firstRow
			&& region.BottomRow <= lastRow
			&& region.RightChannel < row.Pattern.ChannelCount)
		{
			int targetRows = majorRows;
			while (targetRows <= region.RowCount)
				targetRows *= 2;

			selection.SetRegion(
				new PatternSelectionRegion(
					region.TopRow,
					region.LeftChannel,
					Math.Min(
						lastRow,
						region.TopRow + targetRows - 1),
					region.RightChannel));
			return;
		}

		selection.SetRegion(
			new PatternSelectionRegion(
				cursor.Row,
				cursor.Channel,
				Math.Min(
					lastRow,
					cursor.Row + majorRows - 1),
				cursor.Channel));
	}

	public static void SelectColumnOrPattern(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		PatternSelectionState selection)
	{
		Validate(context, cursor, selection);

		PatternEditorRow row =
			context.GetRow(cursor.Row);
		(int firstRow, int lastRow) =
			context.GetPatternDisplayBounds(cursor.Row);
		PatternSelectionRegion? current =
			selection.Region;
		bool currentColumnFullySelected =
			current is PatternSelectionRegion region
				&& region.TopRow <= firstRow
				&& region.BottomRow >= lastRow
				&& cursor.Channel >= region.LeftChannel
				&& cursor.Channel <= region.RightChannel;

		if (currentColumnFullySelected)
		{
			selection.SetRegion(
				new PatternSelectionRegion(
					firstRow,
					0,
					lastRow,
					row.Pattern.ChannelCount - 1));
			return;
		}

		selection.SetRegion(
			new PatternSelectionRegion(
				firstRow,
				cursor.Channel,
				lastRow,
				cursor.Channel));
	}

	private static void Validate(
		PatternEditorContext context,
		PatternEffectCursor cursor,
		PatternSelectionState selection)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(selection);

		if (context.Rows.Count == 0)
			throw new InvalidOperationException(
				"The pattern editor context has no selectable rows.");
		if ((uint)cursor.Row >= (uint)context.Rows.Count)
			throw new InvalidOperationException(
				"The editor cursor lies outside the selectable context.");

		PatternEditorRow row =
			context.GetRow(cursor.Row);
		if ((uint)cursor.Channel >= (uint)row.Pattern.ChannelCount)
		{
			throw new InvalidOperationException(
				"The editor cursor channel lies outside the current pattern.");
		}
	}
}
