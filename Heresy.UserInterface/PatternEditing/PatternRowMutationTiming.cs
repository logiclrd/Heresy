using System;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Instrumentation for an Insert/Delete from the native Pattern grid.
/// Times include live UI work, not only the underlying sparse data shift.
/// An operation is also bounded by CellsRepainted and RealizedCells: unlike
/// a full RefreshGrid, it never recreates the latter's Avalonia controls.
/// This transient diagnostic record is never part of song persistence.
/// </summary>
public sealed record PatternRowMutationTiming(
	TimeSpan DocumentMutation,
	TimeSpan CellRefresh,
	TimeSpan Decorations,
	TimeSpan CursorAndFocus,
	TimeSpan Total,
	int CellsRepainted,
	int RealizedCells);
