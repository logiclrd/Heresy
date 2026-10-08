using Avalonia.Input;

namespace Heresy.UserInterface;

/// <summary>
/// Application-level file commands are offered only after the focused control
/// has had the opportunity to handle a bubbling KeyDown event.
/// </summary>
public enum FileMenuCommand
{
	None,
	New,
	Open,
	Save,
	Exit,
}

public static class FileMenuKeyboard
{
	public static bool TryGetCommand(
		Key key,
		KeyModifiers modifiers,
		bool handledByFocusedControl,
		out FileMenuCommand command)
	{
		command = FileMenuCommand.None;
		if (handledByFocusedControl
			|| modifiers != KeyModifiers.Control)
		{
			return false;
		}

		command = key switch
		{
			Key.N => FileMenuCommand.New,
			Key.O => FileMenuCommand.Open,
			Key.S => FileMenuCommand.Save,
			Key.Q => FileMenuCommand.Exit,
			_ => FileMenuCommand.None,
		};
		return command != FileMenuCommand.None;
	}
}
