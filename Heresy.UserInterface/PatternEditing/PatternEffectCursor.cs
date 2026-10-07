using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternCellField
{
	Note,
	Source,
	Volume,
	EffectCommand,
	EffectParameter,
}

public enum ExpandedEffectField
{
	Command,
	Parameter,
	Native,
}

public sealed class PatternEffectCursor
{
	private int? _pendingHighNibble;
	private int _verticalAdvanceGeneration;

	public PatternEffectCursor(
		int row,
		int channel,
		PatternCellField field)
	{
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		Row = row;
		Channel = channel;
		Field = field;
	}

	public int Row { get; private set; }
	public int Channel { get; private set; }
	public PatternCellField Field { get; private set; }
	public bool IsExpanded { get; private set; }
	public int ExpandedEffectIndex { get; private set; } = -1;
	public ExpandedEffectField ExpandedField { get; private set; }
	internal bool HasPendingHighNibble => _pendingHighNibble.HasValue;
	internal int VerticalAdvanceGeneration => _verticalAdvanceGeneration;

	public void SetPosition(
		int row,
		int channel,
		PatternCellField field)
	{
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		Row = row;
		Channel = channel;
		Field = field;
		Collapse();
	}

	public void Clamp(int rowCount, int channelCount)
	{
		if (rowCount < 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));

		if (rowCount == 0)
		{
			Row = 0;
			Channel = Math.Min(Channel, channelCount - 1);
			Collapse();
			return;
		}

		Row = Math.Min(Row, rowCount - 1);
		Channel = Math.Min(Channel, channelCount - 1);
		Collapse();
	}

	public void MoveLeft(DataPatternDefinition pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (pattern.RowCount <= 0)
			return;

		ResetPendingNibble();
		PatternCell? current = pattern.Grid[Row, Channel];
		if (IsExpanded)
		{
			MoveExpanded(current, -1);
			return;
		}

		switch (Field)
		{
			case PatternCellField.Note:
				if (Channel > 0)
				{
					Channel--;
					PatternCell? previous = pattern.Grid[Row, Channel];
					Field = IsSingleNative(previous)
						? PatternCellField.EffectCommand
						: PatternCellField.EffectParameter;
				}
				break;

			case PatternCellField.Source:
				Field = PatternCellField.Note;
				break;

			case PatternCellField.Volume:
				Field = PatternCellField.Source;
				break;

			case PatternCellField.EffectCommand:
				Field = PatternCellField.Volume;
				break;

			case PatternCellField.EffectParameter:
				Field = PatternCellField.EffectCommand;
				break;
		}
	}

	public void MoveRight(DataPatternDefinition pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (pattern.RowCount <= 0)
			return;

		ResetPendingNibble();
		PatternCell? current = pattern.Grid[Row, Channel];
		if (IsExpanded)
		{
			MoveExpanded(current, 1);
			return;
		}

		switch (Field)
		{
			case PatternCellField.Note:
				Field = PatternCellField.Source;
				break;

			case PatternCellField.Source:
				Field = PatternCellField.Volume;
				break;

			case PatternCellField.Volume:
				Field = PatternCellField.EffectCommand;
				break;

			case PatternCellField.EffectCommand:
				if (IsSingleNative(current))
				{
					if (Channel + 1 < pattern.ChannelCount)
					{
						Channel++;
						Field = PatternCellField.Note;
					}
				}
				else
				{
					Field = PatternCellField.EffectParameter;
				}
				break;

			case PatternCellField.EffectParameter:
				if (Channel + 1 < pattern.ChannelCount)
				{
					Channel++;
					Field = PatternCellField.Note;
				}
				break;
		}
	}

	public void MoveLeft(
		int rowCount,
		int channelCount,
		PatternCell? cell = null)
	{
		ValidateBounds(rowCount, channelCount);
		ResetPendingNibble();

		if (IsExpanded)
		{
			MoveExpanded(cell, -1);
			return;
		}

		switch (Field)
		{
			case PatternCellField.Note:
				if (Channel > 0)
				{
					Channel--;
					Field = PatternCellField.EffectParameter;
				}
				break;
			case PatternCellField.Source:
				Field = PatternCellField.Note;
				break;

			case PatternCellField.Volume:
				Field = PatternCellField.Source;
				break;

			case PatternCellField.EffectCommand:
				Field = PatternCellField.Volume;
				break;
			case PatternCellField.EffectParameter:
				Field = PatternCellField.EffectCommand;
				break;
		}
	}

	public void MoveRight(
		int rowCount,
		int channelCount,
		PatternCell? cell = null)
	{
		ValidateBounds(rowCount, channelCount);
		ResetPendingNibble();

		if (IsExpanded)
		{
			MoveExpanded(cell, 1);
			return;
		}

		switch (Field)
		{
			case PatternCellField.Note:
				Field = PatternCellField.Source;
				break;

			case PatternCellField.Source:
				Field = PatternCellField.Volume;
				break;

			case PatternCellField.Volume:
				Field = PatternCellField.EffectCommand;
				break;
			case PatternCellField.EffectCommand:
				Field = PatternCellField.EffectParameter;
				break;
			case PatternCellField.EffectParameter:
				if (Channel + 1 < channelCount)
				{
					Channel++;
					Field = PatternCellField.Note;
				}
				break;
		}
	}

	public void MoveChannel(
		int channelCount,
		int delta)
	{
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		if (delta is not -1 and not 1)
			throw new ArgumentOutOfRangeException(nameof(delta));

		ResetPendingNibble();
		if (IsExpanded)
			Collapse();

		Channel =
			Math.Clamp(
				Channel + delta,
				0,
				channelCount - 1);
	}

	public void MoveUp(int rowCount)
	{
		if (rowCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (IsExpanded)
			Collapse();
		ResetPendingNibble();
		Row = Math.Max(0, Row - 1);
	}

	public void MoveDown(int rowCount)
	{
		if (rowCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (IsExpanded)
			Collapse();
		ResetPendingNibble();
		_verticalAdvanceGeneration++;
		Row = Math.Min(rowCount - 1, Row + 1);
	}

	public void HandleEnter(PatternCell? cell)
	{
		ResetPendingNibble();
		if (IsExpanded)
		{
			Collapse();
			return;
		}

		if (cell is not null && cell.Effects.Count > 1)
			Expand(cell);
	}

	public void Expand(PatternCell cell)
	{
		ArgumentNullException.ThrowIfNull(cell);
		if (cell.Effects.Count == 0)
			return;

		IsExpanded = true;
		ExpandedEffectIndex = 0;
		if (PatternEffectCodec.TryDecodeTracker(
			cell.Effects[0],
			out _,
			out _))
		{
			ExpandedField =
				Field == PatternCellField.EffectParameter
					? ExpandedEffectField.Parameter
					: ExpandedEffectField.Command;
		}
		else
		{
			ExpandedField = ExpandedEffectField.Native;
		}
		ResetPendingNibble();
	}

	public void MoveToFirstEffect(PatternCell cell)
	{
		MoveToEffect(cell, 0);
	}

	public void MoveToLastEffect(PatternCell cell)
	{
		ArgumentNullException.ThrowIfNull(cell);
		if (cell.Effects.Count == 0)
			return;
		MoveToEffect(cell, cell.Effects.Count - 1);
	}

	private void MoveToEffect(PatternCell cell, int index)
	{
		ArgumentNullException.ThrowIfNull(cell);
		if (!IsExpanded || cell.Effects.Count == 0)
			return;

		PatternEffect effect = cell.Effects[index];
		if (!PatternEffectCodec.IsTrackerStyle(effect))
		{
			SetExpandedSelection(
				cell,
				index,
				ExpandedEffectField.Native);
			return;
		}

		ExpandedEffectField preferred =
			ExpandedField == ExpandedEffectField.Parameter
				? ExpandedEffectField.Parameter
				: ExpandedEffectField.Command;
		SetExpandedSelection(cell, index, preferred);
	}

	public void Collapse()
	{
		IsExpanded = false;
		ExpandedEffectIndex = -1;
		ExpandedField = default;
		ResetPendingNibble();
	}

	public void SetExpandedSelection(
		PatternCell cell,
		int effectIndex,
		ExpandedEffectField field)
	{
		ArgumentNullException.ThrowIfNull(cell);
		if ((uint)effectIndex >= (uint)cell.Effects.Count)
			throw new ArgumentOutOfRangeException(nameof(effectIndex));

		bool tracker =
			PatternEffectCodec.IsTrackerStyle(
				cell.Effects[effectIndex]);
		if (tracker && field == ExpandedEffectField.Native)
			throw new ArgumentException("Tracker effects use command/parameter fields.", nameof(field));
		if (!tracker && field != ExpandedEffectField.Native)
			throw new ArgumentException("Native effects use one whole-tab field.", nameof(field));

		IsExpanded = true;
		ExpandedEffectIndex = effectIndex;
		ExpandedField = field;
		ResetPendingNibble();
	}

	internal void SetPendingHighNibble(int value)
		=> _pendingHighNibble = value;

	internal void ResetPendingNibble()
		=> _pendingHighNibble = null;

	internal void AdvanceAfterCollapsedEntry(int rowCount)
	{
		ResetPendingNibble();
		_verticalAdvanceGeneration++;
		Row = Math.Min(rowCount - 1, Row + 1);
	}

	internal void RemapRow(int row)
	{
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));
		Row = row;
	}

	internal void ClampChannelPreservingField(int channelCount)
	{
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		Channel = Math.Min(Channel, channelCount - 1);
	}

	private void MoveExpanded(PatternCell? cell, int direction)
	{
		if (cell is null)
			throw new ArgumentNullException(nameof(cell));

		List<(int Index, ExpandedEffectField Field)> stops = BuildStops(cell);
		int current =
			stops.FindIndex(stop =>
				stop.Index == ExpandedEffectIndex
					&& stop.Field == ExpandedField);
		if (current < 0)
			current = 0;

		int next = Math.Clamp(current + direction, 0, stops.Count - 1);
		ExpandedEffectIndex = stops[next].Index;
		ExpandedField = stops[next].Field;
	}

	private static bool IsSingleNative(PatternCell? cell)
		=> cell is not null
			&& cell.Effects.Count == 1
			&& !PatternEffectCodec.IsTrackerStyle(
				cell.Effects[0]);

	private static List<(int Index, ExpandedEffectField Field)> BuildStops(
		PatternCell cell)
	{
		List<(int, ExpandedEffectField)> result = [];
		for (int index = 0; index < cell.Effects.Count; index++)
		{
			if (PatternEffectCodec.IsTrackerStyle(
				cell.Effects[index]))
			{
				result.Add((index, ExpandedEffectField.Command));
				result.Add((index, ExpandedEffectField.Parameter));
			}
			else
			{
				result.Add((index, ExpandedEffectField.Native));
			}
		}
		return result;
	}

	private void ValidateBounds(int rowCount, int channelCount)
	{
		if (rowCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		if ((uint)Row >= (uint)rowCount || (uint)Channel >= (uint)channelCount)
			throw new InvalidOperationException("The cursor lies outside the pattern dimensions.");
	}
}
