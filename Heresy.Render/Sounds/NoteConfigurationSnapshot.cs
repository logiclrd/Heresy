namespace Heresy.Render.Sounds;

/// <summary>
/// Immutable per-note configuration captured from an ISound at note start.
/// Later changes to the sound definition do not alter an already-playing voice.
/// </summary>
public sealed record NoteConfigurationSnapshot(NewNotePolicy NewNotePolicy)
{
	public static NoteConfigurationSnapshot Default { get; } =
		new(NewNotePolicy.Cut);
}
