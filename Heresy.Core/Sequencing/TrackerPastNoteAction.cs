namespace Heresy.Core.Sequencing;

/// <summary>
/// Impulse Tracker S70/S71/S72 action applied to all past voices that originated
/// from one physical tracker channel.
/// </summary>
public enum TrackerPastNoteAction : byte
{
	Cut = 0,
	Off = 1,
	Fade = 2,
}
