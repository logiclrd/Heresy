using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

/// <summary>
/// One pattern invocation in a data-driven sequence. StartRow skips complete
/// pattern rows without executing them; their timing is evaluated from the
/// sequencing state in effect on entry.
/// </summary>
public sealed record SequenceEntry
{
	public SequenceEntry(ObjectId patternId, int startRow = 0)
	{
		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		PatternId = patternId;
		StartRow = startRow;
	}

	public ObjectId PatternId { get; }

	public int StartRow { get; }

	public void Deconstruct(out ObjectId patternId, out int startRow)
	{
		patternId = PatternId;
		startRow = StartRow;
	}
}
