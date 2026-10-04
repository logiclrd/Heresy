namespace Heresy.Render.Sounds;

/// <summary>
/// Action applied to an existing voice when its physical playback channel
/// starts a replacement note.
/// </summary>
public enum NewNoteAction
{
	Cut = 0,
	Continue,
	Off,
	Fade,
}
