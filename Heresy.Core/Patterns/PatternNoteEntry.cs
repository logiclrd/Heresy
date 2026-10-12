using System;
using System.Text.Json.Serialization;

using Heresy.Core.Objects;

namespace Heresy.Core.Patterns;

/// <summary>
/// Value stored in the note column of a data-driven pattern cell. This is an
/// editor-facing representation; it is translated to lower-level NoteCommands
/// when the pattern generates its raw note schedule.
/// </summary>
public abstract record PatternNoteEntry;

public sealed record StartPatternNote : PatternNoteEntry
{
	public StartPatternNote(
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		bool mixdown = false,
		ObjectId? eventFilterDefinitionId = null)
		: this(
			ObjectId.None,
			pitchMultiplier,
			playbackSpeedMultiplier,
			mixdown,
			eventFilterDefinitionId)
	{
	}

	[JsonConstructor]
	public StartPatternNote(
		ObjectId sourceId,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		bool mixdown = false,
		ObjectId? eventFilterDefinitionId = null)
	{
		SourceId = sourceId;
		EventFilterDefinitionId = eventFilterDefinitionId is { IsNone: true }
			? throw new ArgumentOutOfRangeException(nameof(eventFilterDefinitionId))
			: eventFilterDefinitionId;
		PitchMultiplier = ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier));
		PlaybackSpeedMultiplier = ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier));
		Mixdown = mixdown;
	}

	public ObjectId SourceId { get; }
	public double PitchMultiplier { get; }
	public double PlaybackSpeedMultiplier { get; }
	public bool Mixdown { get; }

	/// <summary>Optional event Filter definition, separate from Instrument
	/// tone inheritance. Serialized with the Pattern note and resolved by
	/// prepared snapshot playback to a fresh default configured use.</summary>
	public ObjectId? EventFilterDefinitionId { get; }

	private static double ValidateMultiplier(double value, string paramName)
	{
		if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(paramName);

		return value;
	}
}

public sealed record PatternNoteOff : PatternNoteEntry;

public sealed record PatternNoteCut : PatternNoteEntry;
