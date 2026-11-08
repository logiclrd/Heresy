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
/// Shift+piano in the Note field previews; Ctrl/Alt/Meta remain reserved
/// for editing commands. An already previewing key remains preview-only
/// until its own release, even if Shift changes mid-press.
/// </summary>
public static class PatternPreviewKeyRouting
{
    public static bool IsPreviewOnly(
        HeldNotePreviewKeyState state, PhysicalKey key,
        KeyModifiers modifiers)
        => PatternNoteKeyboard.TryGetSemitoneOffset(key, out _)
            && (state.IsPreviewKeyActive(key)
                || ((modifiers & (KeyModifiers.Control
                    | KeyModifiers.Alt | KeyModifiers.Meta)) == 0
                    && (modifiers & KeyModifiers.Shift) != 0));
}
