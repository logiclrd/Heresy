namespace Heresy.Core.Sequencing;

/// <summary>
/// Generic action applied to a playing note when a replacement note starts.
/// Tracker commands such as S73-S76 resolve to this semantic form before
/// reaching the renderer.
/// </summary>
public enum NoteDisplacementAction
{
	Cut = 0,
	Continue = 1,
	Off = 2,
	Fade = 3,
}
