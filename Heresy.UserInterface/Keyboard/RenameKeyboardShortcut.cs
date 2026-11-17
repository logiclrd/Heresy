using Avalonia.Input;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// Shared keyboard gate for rename actions that already exist in the UI.
/// Restrict F2 to an unmodified, unhandled key event so editors retain
/// their own text editing and any platform-specific modified commands.
/// </summary>
public static class RenameKeyboardShortcut
{
	public static bool IsRequested(
		Key key, KeyModifiers modifiers, bool alreadyHandled)
		=> !alreadyHandled && key == Key.F2
			&& modifiers == KeyModifiers.None;
}
