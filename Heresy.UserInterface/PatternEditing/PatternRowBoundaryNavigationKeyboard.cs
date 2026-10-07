using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes the tracker shortcuts that jump to the first or last editable row
/// while preserving the current channel and field.
/// </summary>
public static class PatternRowBoundaryNavigationKeyboard
{
	public static bool TryGetFirst(
		Key key,
		KeyModifiers modifiers,
		out bool first)
	{
		first = false;

		KeyModifiers required = KeyModifiers.Control;
		KeyModifiers blocked =
			KeyModifiers.Shift
				| KeyModifiers.Alt
				| KeyModifiers.Meta;
		if ((modifiers & required) == 0
			|| (modifiers & blocked) != 0)
		{
			return false;
		}

		switch (key)
		{
			case Key.PageUp:
				first = true;
				return true;

			case Key.PageDown:
				first = false;
				return true;

			default:
				return false;
		}
	}
}
