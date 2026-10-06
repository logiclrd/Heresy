using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Framework-independent mutation commands for one pattern cell's ordered
/// semantic effect stack. Keyboard shortcuts, context menus and drag reordering
/// all route through this class.
/// </summary>
public static class PatternEffectStackEditor
{
	public static bool InsertBefore(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor)
		=> Insert(workspace, pattern, cursor, after: false);

	public static bool InsertAfter(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor)
		=> Insert(workspace, pattern, cursor, after: true);

	public static bool Delete(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor)
	{
		Validate(workspace, pattern, cursor);

		PatternCell? cell =
			pattern.Grid[cursor.Row, cursor.Channel];
		if (cell is null || cell.Effects.Count == 0)
			return false;

		int index = GetSelectedIndex(cursor, cell);
		PatternEffect removed = cell.Effects[index];
		cell.Effects.RemoveAt(index);

		if (cell.IsEmpty)
			pattern.Grid.ClearCell(cursor.Row, cursor.Channel);

		workspace.Document.MarkChanged(
			affectsAudio: removed is not EmptyTrackerPatternEffect);

		if (cell.Effects.Count <= 1)
		{
			cursor.SetPosition(
				cursor.Row,
				cursor.Channel,
				PatternCellField.EffectCommand);
			return true;
		}

		int nextIndex = Math.Min(index, cell.Effects.Count - 1);
		cursor.SetExpandedSelection(
			cell,
			nextIndex,
			FirstField(cell.Effects[nextIndex]));
		return true;
	}

	public static bool MoveSelected(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		int delta)
	{
		Validate(workspace, pattern, cursor);
		if (delta == 0)
			return false;

		PatternCell? cell =
			pattern.Grid[cursor.Row, cursor.Channel];
		if (cell is null
			|| cell.Effects.Count <= 1
			|| !cursor.IsExpanded)
		{
			return false;
		}

		int target =
			Math.Clamp(
				cursor.ExpandedEffectIndex + delta,
				0,
				cell.Effects.Count - 1);
		return MoveSelectedTo(
			workspace,
			pattern,
			cursor,
			target);
	}

	public static bool MoveSelectedTo(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		int targetIndex)
	{
		Validate(workspace, pattern, cursor);

		PatternCell? cell =
			pattern.Grid[cursor.Row, cursor.Channel];
		if (cell is null
			|| cell.Effects.Count <= 1
			|| !cursor.IsExpanded)
		{
			return false;
		}

		if ((uint)targetIndex >= (uint)cell.Effects.Count)
			throw new ArgumentOutOfRangeException(nameof(targetIndex));

		int sourceIndex = cursor.ExpandedEffectIndex;
		if (sourceIndex == targetIndex)
			return false;

		PatternEffect selected = cell.Effects[sourceIndex];
		ExpandedEffectField preferred = cursor.ExpandedField;
		cell.Effects.RemoveAt(sourceIndex);
		cell.Effects.Insert(targetIndex, selected);

		workspace.Document.MarkChanged(
			affectsAudio: true);
		cursor.SetExpandedSelection(
			cell,
			targetIndex,
			CompatibleField(selected, preferred));
		return true;
	}

	private static bool Insert(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		bool after)
	{
		Validate(workspace, pattern, cursor);
		if (cursor.Field is not (
			PatternCellField.EffectCommand
			or PatternCellField.EffectParameter))
		{
			return false;
		}

		PatternCell cell =
			pattern.Grid.GetOrCreateCell(
				cursor.Row,
				cursor.Channel);

		int index;
		if (cell.Effects.Count == 0)
		{
			index = 0;
		}
		else
		{
			index = GetSelectedIndex(cursor, cell);
			if (after)
				index++;
		}

		cell.Effects.Insert(
			index,
			new EmptyTrackerPatternEffect());

		// The inserted slot is deliberately musically inert.
		workspace.Document.MarkChanged(
			affectsAudio: false);

		if (cell.Effects.Count == 1)
		{
			cursor.SetPosition(
				cursor.Row,
				cursor.Channel,
				PatternCellField.EffectCommand);
		}
		else
		{
			cursor.SetExpandedSelection(
				cell,
				index,
				ExpandedEffectField.Command);
		}

		return true;
	}

	private static int GetSelectedIndex(
		PatternEffectCursor cursor,
		PatternCell cell)
	{
		if (cursor.IsExpanded)
		{
			if ((uint)cursor.ExpandedEffectIndex
				>= (uint)cell.Effects.Count)
			{
				throw new InvalidOperationException(
					"The expanded effect cursor no longer identifies a stack member.");
			}

			return cursor.ExpandedEffectIndex;
		}

		return 0;
	}

	private static ExpandedEffectField FirstField(
		PatternEffect effect)
		=> PatternEffectCodec.IsTrackerStyle(effect)
			? ExpandedEffectField.Command
			: ExpandedEffectField.Native;

	private static ExpandedEffectField CompatibleField(
		PatternEffect effect,
		ExpandedEffectField preferred)
	{
		if (!PatternEffectCodec.IsTrackerStyle(effect))
			return ExpandedEffectField.Native;

		return preferred == ExpandedEffectField.Parameter
			? ExpandedEffectField.Parameter
			: ExpandedEffectField.Command;
	}

	private static void Validate(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);

		if (!workspace.Document.TryGet(
			pattern.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, pattern))
		{
			throw new InvalidOperationException(
				"The pattern is not part of the active song document.");
		}

		if ((uint)cursor.Row >= (uint)pattern.RowCount
			|| (uint)cursor.Channel >= (uint)pattern.ChannelCount)
		{
			throw new InvalidOperationException(
				"The effect cursor lies outside the pattern dimensions.");
		}
	}
}
