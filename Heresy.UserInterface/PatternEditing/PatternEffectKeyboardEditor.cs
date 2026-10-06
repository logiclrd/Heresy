using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public readonly record struct PatternEffectInputResult(
	bool Changed,
	bool Rejected);

public static class PatternEffectKeyboardEditor
{
	public static PatternEffectInputResult Type(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		char value)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);

		if (!workspace.Document.TryGet(pattern.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, pattern))
		{
			throw new InvalidOperationException(
				"The pattern is not part of the active song document.");
		}

		if (cursor.Field is not (
			PatternCellField.EffectCommand
			or PatternCellField.EffectParameter))
		{
			return new PatternEffectInputResult(false, false);
		}

		PatternCell? cell = pattern.Grid[cursor.Row, cursor.Channel];
		if (!cursor.IsExpanded && (cell?.Effects.Count ?? 0) > 1)
			return new PatternEffectInputResult(false, true);

		int effectIndex = cursor.IsExpanded
			? cursor.ExpandedEffectIndex
			: 0;

		if (cursor.IsExpanded)
		{
			if (cell is null
				|| (uint)effectIndex >= (uint)cell.Effects.Count
				|| cursor.ExpandedField == ExpandedEffectField.Native)
			{
				return new PatternEffectInputResult(false, true);
			}
		}

		PatternEffect? existing =
			cell is not null && effectIndex < cell.Effects.Count
				? cell.Effects[effectIndex]
				: null;

		char? command = null;
		byte parameter = 0;
		if (existing is not null
			&& !PatternEffectCodec.TryDecodeTrackerSlot(
				existing,
				out command,
				out parameter))
		{
			return new PatternEffectInputResult(false, true);
		}

		PatternCellField field =
			cursor.IsExpanded
				? cursor.ExpandedField == ExpandedEffectField.Command
					? PatternCellField.EffectCommand
					: PatternCellField.EffectParameter
				: cursor.Field;

		if (field == PatternCellField.EffectCommand)
		{
			if (!char.IsLetter(value))
				return new PatternEffectInputResult(false, true);

			if (!PatternEffectCodec.TryCreateTracker(
				char.ToUpperInvariant(value),
				parameter,
				out PatternEffect? replacement)
				|| replacement is null)
			{
				return new PatternEffectInputResult(false, true);
			}

			bool changed =
				SetEffect(
					workspace,
					pattern,
					cursor.Row,
					cursor.Channel,
					effectIndex,
					replacement);
			cursor.ResetPendingNibble();
			if (!cursor.IsExpanded)
				cursor.AdvanceAfterCollapsedEntry(pattern.RowCount);
			return new PatternEffectInputResult(changed, false);
		}

		if (existing is null)
			return new PatternEffectInputResult(false, true);

		if (value == '.')
		{
			if (!TryCreateTrackerSlot(
				command,
				0,
				out PatternEffect? replacement)
				|| replacement is null)
			{
				return new PatternEffectInputResult(false, true);
			}

			bool changed =
				SetEffect(
					workspace,
					pattern,
					cursor.Row,
					cursor.Channel,
					effectIndex,
					replacement);
			cursor.ResetPendingNibble();
			if (!cursor.IsExpanded)
				cursor.AdvanceAfterCollapsedEntry(pattern.RowCount);
			return new PatternEffectInputResult(changed, false);
		}

		int nibble = HexNibble(value);
		if (nibble < 0)
			return new PatternEffectInputResult(false, true);

		byte nextParameter;
		bool complete;
		if (!cursor.HasPendingHighNibble)
		{
			nextParameter =
				(byte)((nibble << 4) | (parameter & 0x0F));
			cursor.SetPendingHighNibble(nibble);
			complete = false;
		}
		else
		{
			nextParameter =
				(byte)((parameter & 0xF0) | nibble);
			cursor.ResetPendingNibble();
			complete = true;
		}

		if (!TryCreateTrackerSlot(
			command,
			nextParameter,
			out PatternEffect? next)
			|| next is null)
		{
			cursor.ResetPendingNibble();
			return new PatternEffectInputResult(false, true);
		}

		bool parameterChanged =
			SetEffect(
				workspace,
				pattern,
				cursor.Row,
				cursor.Channel,
				effectIndex,
				next);

		if (complete && !cursor.IsExpanded)
			cursor.AdvanceAfterCollapsedEntry(pattern.RowCount);

		return new PatternEffectInputResult(parameterChanged, false);
	}

	private static bool SetEffect(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		int row,
		int channel,
		int effectIndex,
		PatternEffect effect)
	{
		PatternCell cell = pattern.Grid.GetOrCreateCell(row, channel);

		if (effectIndex == cell.Effects.Count)
		{
			cell.Effects.Add(effect);
			workspace.Document.MarkChanged(affectsAudio: true);
			return true;
		}

		if ((uint)effectIndex >= (uint)cell.Effects.Count)
			throw new ArgumentOutOfRangeException(nameof(effectIndex));

		if (Equals(cell.Effects[effectIndex], effect))
			return false;

		cell.Effects[effectIndex] = effect;
		workspace.Document.MarkChanged(affectsAudio: true);
		return true;
	}

	private static bool TryCreateTrackerSlot(
		char? command,
		byte parameter,
		out PatternEffect? effect)
	{
		if (!command.HasValue)
		{
			effect = new EmptyTrackerPatternEffect(parameter);
			return true;
		}

		return PatternEffectCodec.TryCreateTracker(
			command.Value,
			parameter,
			out effect);
	}

	private static int HexNibble(char value)
	{
		if (value is >= '0' and <= '9')
			return value - '0';

		char upper = char.ToUpperInvariant(value);
		if (upper is >= 'A' and <= 'F')
			return upper - 'A' + 10;

		return -1;
	}
}
