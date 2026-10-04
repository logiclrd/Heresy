using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

/// <summary>
/// One pattern invocation in a data-driven sequence. StartRow skips pattern
/// material without executing it; its timing is evaluated from the sequencing
/// state in effect on entry.
/// </summary>
public sealed record SequenceEntry
{
	public SequenceEntry(ObjectId patternId, double startRow = 0.0)
	{
		if (double.IsNaN(startRow) || double.IsInfinity(startRow) || startRow < 0.0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		PatternId = patternId;
		StartRow = startRow;
	}

	public ObjectId PatternId { get; }

	public double StartRow { get; }

	public void Deconstruct(out ObjectId patternId, out double startRow)
	{
		patternId = PatternId;
		startRow = StartRow;
	}
}
