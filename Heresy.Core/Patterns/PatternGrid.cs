using System;
using System.Collections.Generic;

namespace Heresy.Core.Patterns;

/// <summary>
/// Mutable rectangular grid backing a data-driven pattern. Empty cells are
/// represented by null references so resizing and traversal remain inexpensive.
/// </summary>
public sealed class PatternGrid
{
	private PatternCell?[,] _cells;

	public PatternGrid(int rowCount, int channelCount)
	{
		ValidateDimensions(rowCount, channelCount);
		_cells = new PatternCell?[rowCount, channelCount];
	}

	public int RowCount => _cells.GetLength(0);
	public int ChannelCount => _cells.GetLength(1);

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
			_cells[row, channel] = value;
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
		}

		return cell;
	}

	public void ClearCell(int row, int channel)
	{
		ValidateAddress(row, channel);
		_cells[row, channel] = null;
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

		_cells = replacement;
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
