using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes tracker shortcuts that move the current Source selection without
/// modifying pattern data.
/// </summary>
public static class PatternSourceNavigationKeyboard
{
	public static bool TryGetDelta(
		char value,
		out int delta)
	{
		delta = value switch
		{
			'<' => -1,
			'>' => 1,
			_ => 0,
		};
		return delta != 0;
	}

	public static bool TryGetDelta(
		Key key,
		KeyModifiers modifiers,
		out int delta)
	{
		delta = 0;

		KeyModifiers required = KeyModifiers.Control;
		KeyModifiers blocked =
			KeyModifiers.Alt
				| KeyModifiers.Meta
				| KeyModifiers.Shift;
		if ((modifiers & required) == 0
			|| (modifiers & blocked) != 0)
		{
			return false;
		}

		delta = key switch
		{
			Key.Up => -1,
			Key.Down => 1,
			_ => 0,
		};
		return delta != 0;
	}
}
