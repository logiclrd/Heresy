using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Traditional tracker note-entry step. Only unmodified Alt plus a physical
/// top-row digit selects the step; Ctrl+Alt digits remain chord shortcuts.
/// </summary>
public static class PatternSkipKeyboard
{
	public static bool TryGetValue(
		PhysicalKey key,
		KeyModifiers modifiers,
		out int skipRows)
	{
		skipRows = key switch
		{
			PhysicalKey.Digit0 => 0,
			PhysicalKey.Digit1 => 1,
			PhysicalKey.Digit2 => 2,
			PhysicalKey.Digit3 => 3,
			PhysicalKey.Digit4 => 4,
			PhysicalKey.Digit5 => 5,
			PhysicalKey.Digit6 => 6,
			PhysicalKey.Digit7 => 7,
			PhysicalKey.Digit8 => 8,
			PhysicalKey.Digit9 => 9,
			_ => -1,
		};
		return modifiers == KeyModifiers.Alt && skipRows >= 0;
	}
}

/// <summary>
/// A held tracker piano key belongs exclusively to live preview while Caps
/// Lock is physically down, including auto-repeat. If Caps is released before
/// the piano key, its remaining repeats still cannot turn into note entries.
/// </summary>
public static class PatternPreviewKeyRouting
{
	public static bool IsPreviewOnly(
		HeldNotePreviewKeyState state,
		PhysicalKey key)
		=> PatternNoteKeyboard.TryGetSemitoneOffset(key, out _)
			&& (state.CapsLockHeld || state.IsPreviewKeyActive(key));
}
