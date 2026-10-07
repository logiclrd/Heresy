using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes the whole-tracker corner navigation shortcuts.
/// </summary>
public static class PatternCornerNavigationKeyboard
{
	public static bool TryGetTopLeft(
		Key key,
		KeyModifiers modifiers,
		out bool topLeft)
	{
		topLeft = false;

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
			case Key.Home:
				topLeft = true;
				return true;

			case Key.End:
				topLeft = false;
				return true;

			default:
				return false;
		}
	}
}
