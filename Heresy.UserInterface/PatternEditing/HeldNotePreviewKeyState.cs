using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public abstract record HeldNotePreviewAction;

public sealed record StartHeldNotePreviewAction(
	PhysicalKey Key,
	int VoiceId,
	double PitchMultiplier,
	bool StartsSession)
	: HeldNotePreviewAction;

public sealed record ReleaseHeldNotePreviewAction(
	PhysicalKey Key,
	int VoiceId)
	: HeldNotePreviewAction;

public sealed class HeldNotePreviewKeyState
{
	public bool CapsLockHeld => false;
	public int ActiveNoteCount => 0;

	public HeldNotePreviewAction? KeyDown(
		PhysicalKey key,
		int baseOctave)
		=> null;

	public HeldNotePreviewAction? KeyUp(
		PhysicalKey key)
		=> null;
}
