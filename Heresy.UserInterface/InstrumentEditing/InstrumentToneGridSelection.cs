using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// Rectangle in projected (descending-tone) row order and fixed grid columns.
/// Stored by tone index rather than Avalonia row-container identity so virtual
/// scrolling, item recycling and reprojection cannot alter the selection.
/// The reserved entry row has a distinct null-index identity.
/// </summary>
public sealed class InstrumentToneGridSelection
{
	public const int ColumnCount = 11;

	public readonly record struct Cell(int? ToneIndex, int Column);
	public Cell? Anchor { get; private set; }
	public Cell? Extent { get; private set; }
	public Cell? Focused { get; private set; }

	public void Clear()
	{
		Anchor = null;
		Extent = null;
		Focused = null;
	}

	/// <summary>Ordinary click/Tab/focus traversal preserves a range when
	/// landing inside it; outside focus starts a single-cell selection.</summary>
	public void Focus(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		if (!Contains(cell, rows))
			Anchor = Extent = cell;
		Focused = cell;
	}

	/// <summary>Shift+click or Shift+Arrow extends the rectangle from its
	/// original anchor. The special boundary Shift+Left is separately handled
	/// by BeginAt for a left-hand cell newly becoming the anchor.</summary>
	public void Extend(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		if (Anchor is null || RowPosition(rows, Anchor.Value.ToneIndex) < 0)
			Anchor = Focused ?? cell;
		Extent = cell;
		Focused = cell;
	}

	public void BeginAt(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		Anchor = Extent = Focused = cell;
	}

	/// <summary>Move in visual row order; upward moves to higher tones.</summary>
	public Cell? Neighbor(int rowDelta, int columnDelta,
		IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Focused is not Cell focused)
			return null;
		int position = RowPosition(rows, focused.ToneIndex);
		if (position < 0)
			return null;
		int nextRow = position + rowDelta;
		int nextColumn = focused.Column + columnDelta;
		if ((uint)nextRow >= (uint)rows.Count ||
			(uint)nextColumn >= ColumnCount)
			return null;
		return new Cell(rows[nextRow].Index, nextColumn);
	}

	public bool Contains(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Anchor is not Cell start || Extent is not Cell end)
			return false;
		int a = RowPosition(rows, start.ToneIndex);
		int b = RowPosition(rows, end.ToneIndex);
		int c = RowPosition(rows, cell.ToneIndex);
		return a >= 0 && b >= 0 && c >= Math.Min(a, b) &&
			c <= Math.Max(a, b) &&
			cell.Column >= Math.Min(start.Column, end.Column) &&
			cell.Column <= Math.Max(start.Column, end.Column);
	}

	/// <summary>Keep surviving tones across changes to Divisions, Offset
	/// or added extra rows; discard obsolete endpoints rather than assigning
	/// them to a new visual row by recycled container index.</summary>
	public void Reconcile(IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Anchor is Cell anchor &&
			RowPosition(rows, anchor.ToneIndex) >= 0 &&
			Extent is Cell end && RowPosition(rows, end.ToneIndex) >= 0)
		{
			if (Focused is Cell focused &&
				RowPosition(rows, focused.ToneIndex) >= 0)
				return;
			Focused = end;
			return;
		}
		if (Focused is Cell surviving &&
			RowPosition(rows, surviving.ToneIndex) >= 0)
		{
			Anchor = Extent = surviving;
			return;
		}
		Clear();
	}

	public static int RowPosition(
		IReadOnlyList<InstrumentToneGridRow> rows, int? toneIndex)
	{
		for (int i = 0; i < rows.Count; i++)
			if (rows[i].Index == toneIndex)
				return i;
		return -1;
	}

	private static void Validate(Cell cell,
		IReadOnlyList<InstrumentToneGridRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);
		if ((uint)cell.Column >= ColumnCount ||
			RowPosition(rows, cell.ToneIndex) < 0)
			throw new ArgumentOutOfRangeException(nameof(cell));
	}
}
