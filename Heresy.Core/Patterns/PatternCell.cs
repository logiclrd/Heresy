using System;
using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Patterns;

/// <summary>
/// One row/channel intersection in a data-driven pattern.
/// </summary>
public sealed class PatternCell
{
	private double? _volume;
	private PatternNoteEntry? _note;

	/// <summary>Fires whenever the actual Note event changes, so a containing
	/// PatternGrid can invalidate its derived note-event bounds.</summary>
	internal event Action? NoteChanged;

	public PatternNoteEntry? Note
	{
		get => _note;
		set
		{
			if (ReferenceEquals(_note, value))
				return;
			_note = value;
			NoteChanged?.Invoke();
		}
	}

	/// <summary>
	/// Optional explicit sound source in the tracker Source column. ObjectId.None
	/// means omitted; note starts then use the channel's remembered source.
	/// </summary>
	public ObjectId SourceId { get; set; } = ObjectId.None;

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
		&& SourceId.IsNone
		&& Volume is null
		&& Effects.Count == 0;
}
