using System;

using Heresy.Render.Envelopes;

namespace Heresy.Render.Sounds;

/// <summary>
/// Immutable per-note configuration captured from an ISound at note start.
/// Later changes to the sound definition do not alter an already-playing voice.
/// </summary>
public sealed record NoteConfigurationSnapshot
{
	public NoteConfigurationSnapshot(
		NewNotePolicy newNotePolicy,
		TimeSpan? noteFadeDuration = null,
		TimeSpan? newNoteFadeDuration = null,
		EnvelopeConfigurationSnapshot? envelopes = null)
	{
		ValidateOptionalDuration(
			noteFadeDuration,
			nameof(noteFadeDuration));
		ValidateOptionalDuration(
			newNoteFadeDuration,
			nameof(newNoteFadeDuration));

		NewNotePolicy = newNotePolicy;
		NoteFadeDuration = noteFadeDuration;
		NewNoteFadeDuration =
			newNoteFadeDuration
			?? (newNotePolicy.Action == NewNoteAction.Fade
				? newNotePolicy.FadeDuration
				: null);
		Envelopes = envelopes ?? EnvelopeConfigurationSnapshot.Empty;
	}

	public NewNotePolicy NewNotePolicy { get; }

	/// <summary>
	/// Immutable envelope curves captured for this note. Mutable enable/disable
	/// position belongs to PlaybackVoice, not to this configuration snapshot.
	/// </summary>
	public EnvelopeConfigurationSnapshot Envelopes { get; }

	/// <summary>
	/// Optional duration used when an explicit tracker past-note fade is
	/// requested. This is intentionally independent of the NNA fade duration.
	/// </summary>
	public TimeSpan? NoteFadeDuration { get; }

	/// <summary>
	/// Optional duration available when the current voice's NNA is overridden
	/// to Fade (for example by tracker S76). If the source's default NNA is
	/// already Fade, its policy duration is used automatically.
	/// </summary>
	public TimeSpan? NewNoteFadeDuration { get; }

	public static NoteConfigurationSnapshot Default { get; } =
		new(NewNotePolicy.Cut);

	private static void ValidateOptionalDuration(
		TimeSpan? duration,
		string parameterName)
	{
		if (duration.HasValue
			&& duration.Value <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}
}
