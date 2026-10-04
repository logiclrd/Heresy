using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Immutable per-note configuration captured from an ISound at note start.
/// Later changes to the sound definition do not alter an already-playing voice.
/// </summary>
public sealed record NoteConfigurationSnapshot
{
	public NoteConfigurationSnapshot(
		NewNotePolicy newNotePolicy,
		TimeSpan? noteFadeDuration = null)
	{
		if (noteFadeDuration.HasValue
			&& noteFadeDuration.Value <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(
				nameof(noteFadeDuration));
		}

		NewNotePolicy = newNotePolicy;
		NoteFadeDuration = noteFadeDuration;
	}

	public NewNotePolicy NewNotePolicy { get; }

	/// <summary>
	/// Optional duration used when an explicit tracker past-note fade is
	/// requested. This is intentionally independent of the NNA Fade duration.
	/// </summary>
	public TimeSpan? NoteFadeDuration { get; }

	public static NoteConfigurationSnapshot Default { get; } =
		new(NewNotePolicy.Cut);
}
