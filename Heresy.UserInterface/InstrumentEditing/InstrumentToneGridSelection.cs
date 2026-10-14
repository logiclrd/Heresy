using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// Whole-row selection in projected (descending-tone) order. A column is
/// retained only for keyboard focus, never for selection or bulk-edit bounds.
/// Stored by tone index rather than Avalonia row-container identity so virtual
/// scrolling, item recycling and reprojection cannot alter the selection.
/// The reserved entry row has a distinct null-index identity.
/// </summary>
public sealed class InstrumentToneGridSelection
{
	public const int ColumnCount = 11;

	/// <summary>TextBox keeps caret motion and Shift+selection until an
	/// unselected caret actually reaches the appropriate horizontal edge.
	/// Up/Down always belongs to grid navigation and bypasses this guard.</summary>
	public static bool CanTransferTextBoundary(
		int caret, int length, bool hasSelection, int columnDelta)
		=> !hasSelection && (columnDelta < 0
			? caret == 0
			: columnDelta > 0 && caret == length);

	public readonly record struct Row(int? ToneIndex);
	public readonly record struct Cell(int? ToneIndex, int Column);
	public Row? Anchor { get; private set; }
	public Row? Extent { get; private set; }
	public Cell? Focused { get; private set; }
	private IReadOnlyList<InstrumentToneGridRow>? _indexedRows;
	private Dictionary<int, int> _positions = [];

	public void Clear()
	{
		Anchor = null;
		Extent = null;
		Focused = null;
	}

	/// <summary>Click/Tab/focus inside any column of an already selected
	/// row preserves the row range; outside focus starts a one-row selection.
	/// The exact cell remains tracked independently for typing.</summary>
	public void Focus(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		if (!Contains(cell, rows))
			Anchor = Extent = new Row(cell.ToneIndex);
		Focused = cell;
	}

	/// <summary>Shift+click and vertical Shift+Arrow extend the inclusive
	/// row span from its original anchor. The endpoint's column is focus-only.</summary>
	public void Extend(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		if (Anchor is null || Position(rows, Anchor.Value.ToneIndex) < 0)
			Anchor = new Row((Focused ?? cell).ToneIndex);
		Extent = new Row(cell.ToneIndex);
		Focused = cell;
	}

	public void BeginAt(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		Validate(cell, rows);
		Anchor = Extent = new Row(cell.ToneIndex);
		Focused = cell;
	}

	/// <summary>Move in visual row order; upward moves to higher tones.</summary>
	public Cell? Neighbor(int rowDelta, int columnDelta,
		IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Focused is not Cell focused)
			return null;
		int position = Position(rows, focused.ToneIndex);
		if (position < 0)
			return null;
		int nextRow = position + rowDelta;
		int nextColumn = focused.Column + columnDelta;
		if ((uint)nextRow >= (uint)rows.Count ||
			(uint)nextColumn >= ColumnCount)
			return null;
		return new Cell(rows[nextRow].Index, nextColumn);
	}

	/// <summary>All selected data rows receive an edit of *any* field on
	/// a selected row, regardless of which column had focus when the
	/// selection began. The reserved entry row never receives bulk edits.</summary>
	public IReadOnlyList<int> EditTargetRows(
		Cell edited, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (edited.ToneIndex is not int ownIndex)
			return [];
		if (!Contains(edited, rows) ||
			Anchor is not Row anchor || Extent is not Row extent)
			return [ownIndex];
		int first = Math.Min(Position(rows, anchor.ToneIndex),
			Position(rows, extent.ToneIndex));
		int last = Math.Max(Position(rows, anchor.ToneIndex),
			Position(rows, extent.ToneIndex));
		List<int> indices = [];
		for (int i = first; i <= last; i++)
			if (rows[i].Index is int index)
				indices.Add(index);
		return indices;
	}

	public bool Contains(Cell cell, IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Anchor is not Row start || Extent is not Row end ||
			(uint)cell.Column >= ColumnCount)
			return false;
		int a = Position(rows, start.ToneIndex);
		int b = Position(rows, end.ToneIndex);
		int c = Position(rows, cell.ToneIndex);
		return a >= 0 && b >= 0 && c >= Math.Min(a, b) &&
			c <= Math.Max(a, b);
	}

	/// <summary>Keep surviving tones across changes to Divisions, Offset
	/// or added extra rows; discard obsolete endpoints rather than assigning
	/// them to a new visual row by recycled container index.</summary>
	public void Reconcile(IReadOnlyList<InstrumentToneGridRow> rows)
	{
		if (Anchor is Row anchor &&
			Position(rows, anchor.ToneIndex) >= 0 &&
			Extent is Row end && Position(rows, end.ToneIndex) >= 0)
		{
			if (Focused is Cell focused &&
				Position(rows, focused.ToneIndex) >= 0)
				return;
			// Endpoints are rows; focused editing still requires an exact
			// column even when its previously focused tone disappeared.
			Focused = new Cell(end.ToneIndex, Focused?.Column ?? 0);
			return;
		}
		if (Focused is Cell surviving &&
			Position(rows, surviving.ToneIndex) >= 0)
		{
			Anchor = Extent = new Row(surviving.ToneIndex);
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

	// Index the current projection once. Contains() is called for every
	// visible column as virtualization recycles rows; a linear search per
	// cell would otherwise become quadratic at the 100,000-row limit.
	private int Position(IReadOnlyList<InstrumentToneGridRow> rows, int? index)
	{
		if (!ReferenceEquals(_indexedRows, rows))
		{
			Dictionary<int, int> next = new(rows.Count);
			for (int i = 0; i < rows.Count; i++)
				next.Add(rows[i].Index ?? -1, i);
			_positions = next;
			_indexedRows = rows;
		}
		return _positions.TryGetValue(index ?? -1, out int position)
			? position : -1;
	}

	private void Validate(Cell cell,
		IReadOnlyList<InstrumentToneGridRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);
		if ((uint)cell.Column >= ColumnCount ||
			Position(rows, cell.ToneIndex) < 0)
			throw new ArgumentOutOfRangeException(nameof(cell));
	}
}
