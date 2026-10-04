using System.Collections.Generic;

namespace Heresy.Core.Patterns;

/// <summary>
/// One row/channel intersection in a data-driven pattern.
/// </summary>
public sealed class PatternCell
{
	public PatternNoteEntry? Note { get; set; }

	/// <summary>
	/// Semantic effects attached to this cell, in application order.
	/// </summary>
	public List<PatternEffect> Effects { get; } = [];

	public bool IsEmpty => Note is null && Effects.Count == 0;
}
