using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Immutable policy captured when a note starts. FadeDuration is meaningful
/// only for Fade and specifies the complete time from full volume to zero.
/// </summary>
public readonly record struct NewNotePolicy
{
	public NewNotePolicy(NewNoteAction action, TimeSpan fadeDuration = default)
	{
		if (action == NewNoteAction.Fade)
		{
			if (fadeDuration <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(fadeDuration));
		}
		else if (fadeDuration != TimeSpan.Zero)
		{
			throw new ArgumentException(
				"Fade duration may only be specified for the Fade new-note action.",
				nameof(fadeDuration));
		}

		Action = action;
		FadeDuration = fadeDuration;
	}

	public NewNoteAction Action { get; }

	public TimeSpan FadeDuration { get; }

	public static NewNotePolicy Cut => new(NewNoteAction.Cut);

	public static NewNotePolicy Continue => new(NewNoteAction.Continue);

	public static NewNotePolicy Off => new(NewNoteAction.Off);

	public static NewNotePolicy Fade(TimeSpan duration)
		=> new(NewNoteAction.Fade, duration);
}
