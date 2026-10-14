using System;
using System.Collections.Generic;

namespace Heresy.Core.Patterns;

/// <summary>
/// The inclusive maximal row/channel occupied by actual Note events. An
/// empty Pattern has a minimum display footprint of 1x1, without inventing
/// a Note event; use HasNoteEvents to distinguish that case.
/// </summary>
public readonly record struct PatternNoteEventBounds(
	int LastRow, int LastChannel, bool HasNoteEvents)
{
	public int RowCount => LastRow + 1;
	public int ChannelCount => LastChannel + 1;
}

/// <summary>
/// Mutable rectangular grid backing a data-driven pattern. Empty cells are
/// represented by null references so resizing and traversal remain inexpensive.
/// </summary>
public sealed class PatternGrid
{
	private PatternCell?[,] _cells;
	private readonly Dictionary<PatternCell, int> _trackedCellReferences = [];
	private bool _noteEventBoundsDirty = true;
	private PatternNoteEventBounds _cachedNoteEventBounds;

	public PatternGrid(int rowCount, int channelCount)
	{
		ValidateDimensions(rowCount, channelCount);
		_cells = new PatternCell?[rowCount, channelCount];
	}

	public int RowCount => _cells.GetLength(0);
	public int ChannelCount => _cells.GetLength(1);

	/// <summary>
	/// Cheap O(1) reads between mutations; scans once after a Note change,
	/// grid replacement, row shift or resize. Never part of song persistence.
	/// </summary>
	public PatternNoteEventBounds NoteEventBounds
	{
		get
		{
			if (_noteEventBoundsDirty)
				RecalculateNoteEventBounds();
			return _cachedNoteEventBounds;
		}
	}

	public PatternCell? this[int row, int channel]
	{
		get
		{
			ValidateAddress(row, channel);
			return _cells[row, channel];
		}
		set
		{
			ValidateAddress(row, channel);
			PatternCell? oldCell = _cells[row, channel];
			if (ReferenceEquals(oldCell, value))
				return;
			if (oldCell is not null)
				Untrack(oldCell);
			_cells[row, channel] = value;
			if (value is not null)
				Track(value);
			InvalidateNoteEventBounds();
		}
	}

	public PatternCell GetOrCreateCell(int row, int channel)
	{
		ValidateAddress(row, channel);

		PatternCell? cell = _cells[row, channel];
		if (cell is null)
		{
			cell = new PatternCell();
			_cells[row, channel] = cell;
			Track(cell);
		}

		return cell;
	}

	public void ClearCell(int row, int channel)
	{
		this[row, channel] = null;
	}

	/// <summary>
	/// Inserts an empty row position at <paramref name="row"/> by shifting cells
	/// downward within one channel, or every channel when <paramref name="channel"/>
	/// is null. The grid dimensions do not change; cells shifted past the bottom
	/// edge are discarded.
	/// </summary>
	public bool InsertRow(int row, int? channel)
	{
		ValidateRow(row);
		ValidateOptionalChannel(channel);

		bool changed = false;
		int firstChannel = channel ?? 0;
		int lastChannel = channel ?? (ChannelCount - 1);
		for (int currentChannel = firstChannel; currentChannel <= lastChannel; currentChannel++)
		{
			for (int currentRow = row; currentRow < RowCount; currentRow++)
			{
				if (_cells[currentRow, currentChannel] is not null)
				{
					changed = true;
					break;
				}
			}

			// The last cell is discarded; shifted references retain their
			// existing subscriptions and are not cloned.
			if (_cells[RowCount - 1, currentChannel] is { } discarded)
				Untrack(discarded);
			for (int currentRow = RowCount - 1; currentRow > row; currentRow--)
				_cells[currentRow, currentChannel] = _cells[currentRow - 1, currentChannel];
			_cells[row, currentChannel] = null;
		}

		InvalidateNoteEventBounds();
		return changed;
	}

	/// <summary>
	/// Deletes the row position at <paramref name="row"/> by shifting cells upward
	/// within one channel, or every channel when <paramref name="channel"/> is
	/// null. The grid dimensions do not change; the bottom position is cleared.
	/// </summary>
	public bool DeleteRow(int row, int? channel)
	{
		ValidateRow(row);
		ValidateOptionalChannel(channel);

		bool changed = false;
		int firstChannel = channel ?? 0;
		int lastChannel = channel ?? (ChannelCount - 1);
		for (int currentChannel = firstChannel; currentChannel <= lastChannel; currentChannel++)
		{
			for (int currentRow = row; currentRow < RowCount; currentRow++)
			{
				if (_cells[currentRow, currentChannel] is not null)
				{
					changed = true;
					break;
				}
			}

			// Only the removed row leaves this grid's membership.
			if (_cells[row, currentChannel] is { } discarded)
				Untrack(discarded);
			for (int currentRow = row; currentRow < RowCount - 1; currentRow++)
				_cells[currentRow, currentChannel] = _cells[currentRow + 1, currentChannel];
			_cells[RowCount - 1, currentChannel] = null;
		}

		InvalidateNoteEventBounds();
		return changed;
	}

	/// <summary>
	/// Resizes the grid, preserving cells in the overlapping region and dropping
	/// cells which fall outside the new dimensions.
	/// </summary>
	public void Resize(int rowCount, int channelCount)
	{
		ValidateDimensions(rowCount, channelCount);

		if (rowCount == RowCount && channelCount == ChannelCount)
			return;

		PatternCell?[,] replacement = new PatternCell?[rowCount, channelCount];
		int rowsToCopy = Math.Min(rowCount, RowCount);
		int channelsToCopy = Math.Min(channelCount, ChannelCount);

		for (int row = 0; row < rowsToCopy; row++)
		{
			for (int channel = 0; channel < channelsToCopy; channel++)
				replacement[row, channel] = _cells[row, channel];
		}

		// Drop listeners for cells truncated in either dimension; kept
		// cells retain exactly the same Note-change subscriptions.
		for (int row = 0; row < RowCount; row++)
		{
			for (int channel = 0; channel < ChannelCount; channel++)
			{
				if (row >= rowCount || channel >= channelCount)
				{
					if (_cells[row, channel] is { } discarded)
						Untrack(discarded);
				}
			}
		}
		_cells = replacement;
		InvalidateNoteEventBounds();
	}

	/// <summary>
	/// Enumerates non-empty cells in row-major order. Cells that have been
	/// allocated but contain no note or effects are skipped.
	/// </summary>
	public IEnumerable<(int Row, int Channel, PatternCell Cell)> EnumerateNonEmptyCells()
	{
		for (int row = 0; row < RowCount; row++)
		{
			for (int channel = 0; channel < ChannelCount; channel++)
			{
				PatternCell? cell = _cells[row, channel];
				if (cell is not null && !cell.IsEmpty)
					yield return (row, channel, cell);
			}
		}
	}

	private void Track(PatternCell cell)
	{
		if (_trackedCellReferences.TryGetValue(cell, out int count))
		{
			_trackedCellReferences[cell] = count + 1;
			return;
		}
		_trackedCellReferences.Add(cell, 1);
		cell.NoteChanged += InvalidateNoteEventBounds;
	}

	private void Untrack(PatternCell cell)
	{
		int count = _trackedCellReferences[cell];
		if (count > 1)
		{
			_trackedCellReferences[cell] = count - 1;
			return;
		}
		_trackedCellReferences.Remove(cell);
		cell.NoteChanged -= InvalidateNoteEventBounds;
	}

	private void InvalidateNoteEventBounds() => _noteEventBoundsDirty = true;

	private void RecalculateNoteEventBounds()
	{
		int lastRow = 0;
		int lastChannel = 0;
		bool hasNotes = false;
		for (int row = 0; row < RowCount; row++)
		{
			for (int channel = 0; channel < ChannelCount; channel++)
			{
				if (_cells[row, channel]?.Note is null)
					continue;
				hasNotes = true;
				lastRow = Math.Max(lastRow, row);
				lastChannel = Math.Max(lastChannel, channel);
			}
		}
		_cachedNoteEventBounds = new PatternNoteEventBounds(
			lastRow, lastChannel, hasNotes);
		_noteEventBoundsDirty = false;
	}

	private void ValidateRow(int row)
	{
		if ((uint)row >= (uint)RowCount)
			throw new ArgumentOutOfRangeException(nameof(row));
	}

	private void ValidateOptionalChannel(int? channel)
	{
		if (channel.HasValue
			&& (uint)channel.Value >= (uint)ChannelCount)
		{
			throw new ArgumentOutOfRangeException(nameof(channel));
		}
	}

	private static void ValidateDimensions(int rowCount, int channelCount)
	{
		if (rowCount < 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
	}

	private void ValidateAddress(int row, int channel)
	{
		if ((uint)row >= (uint)RowCount)
			throw new ArgumentOutOfRangeException(nameof(row));
		if ((uint)channel >= (uint)ChannelCount)
			throw new ArgumentOutOfRangeException(nameof(channel));
	}
}
