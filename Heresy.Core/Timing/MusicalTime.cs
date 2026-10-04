using System;
using System.Collections.Generic;

namespace Heresy.Core.Timing;

/// <summary>
/// A position with both row-relative and wall-time components. RowOffset is
/// intentionally left unresolved until a sequencing context supplies tempo and
/// speed information.
/// </summary>
public readonly record struct MusicalTime
{
	public static readonly MusicalTime Zero = new(TimeSpan.Zero, 0.0);

	public MusicalTime(TimeSpan timeOffset, double rowOffset)
	{
		if (double.IsNaN(rowOffset) || double.IsInfinity(rowOffset))
			throw new ArgumentOutOfRangeException(nameof(rowOffset));

		TimeOffset = timeOffset;
		RowOffset = rowOffset;
	}

	public TimeSpan TimeOffset { get; }

	public double RowOffset { get; }

	public void Deconstruct(out TimeSpan timeOffset, out double rowOffset)
	{
		timeOffset = TimeOffset;
		rowOffset = RowOffset;
	}
}
