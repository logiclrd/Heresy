using System;
using System.Collections.Generic;

namespace Heresy.Core.Patterns;

/// <summary>
/// One row/channel intersection in a data-driven pattern.
/// </summary>
public sealed class PatternCell
{
	private double? _volume;

	public PatternNoteEntry? Note { get; set; }

	/// <summary>
	/// Optional normalized note volume stored directly in the tracker volume
	/// column. When a row starts a note this is folded into StartNoteCommand;
	/// otherwise the pattern processor emits SetNoteVolumeCommand.
	/// </summary>
	public double? Volume
	{
		get => _volume;
		set
		{
			if (value.HasValue
				&& (double.IsNaN(value.Value)
					|| double.IsInfinity(value.Value)
					|| value.Value < 0.0
					|| value.Value > 1.0))
			{
				throw new ArgumentOutOfRangeException(nameof(value));
			}

			_volume = value;
		}
	}

	/// <summary>
	/// Semantic effects attached to this cell, in application order.
	/// </summary>
	public List<PatternEffect> Effects { get; } = [];

	public bool IsEmpty =>
		Note is null
		&& Volume is null
		&& Effects.Count == 0;
}
