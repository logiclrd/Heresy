using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes tracker Tab navigation between Note columns.
/// </summary>
public static class PatternNoteColumnNavigationKeyboard
{
	public static bool TryGetDelta(
		Key key,
		KeyModifiers modifiers,
		out int delta)
	{
		delta = 0;
		if (key != Key.Tab)
			return false;

		KeyModifiers blocked =
			KeyModifiers.Control
				| KeyModifiers.Alt
				| KeyModifiers.Meta;
		if ((modifiers & blocked) != 0)
			return false;

		delta =
			(modifiers & KeyModifiers.Shift) != 0
				? -1
				: 1;
		return true;
	}
}
