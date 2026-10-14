using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Resolves explicit tracker Source-column values for editor UI selection.
/// Deliberately does not apply sequencing source memory: an omitted Source cell
/// has nothing to select in the toolbar.
/// </summary>
public static class PatternSourceSelection
{
	/// <summary>One canonical per-cell Source selection shared with the
	/// flattened-source analysis. Source-column selection takes precedence
	/// over a StartNote inline ID; omission means keep channel memory.</summary>
	public static ObjectId ResolveSourceSelection(PatternCell? cell)
		=> cell is null ? ObjectId.None
			: !cell.SourceId.IsNone ? cell.SourceId
			: cell.Note is StartPatternNote start ? start.SourceId
			: ObjectId.None;

	public static PatternSourceOption? FindExplicitSource(
		PatternCell? cell,
		IReadOnlyList<PatternSourceOption> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);

		if (cell is null || cell.SourceId.IsNone)
			return null;

		for (int i = 0; i < sources.Count; i++)
		{
			if (sources[i].Id == cell.SourceId)
				return sources[i];
		}

		return null;
	}
}
