namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes the tracker pattern-switching text commands.
/// </summary>
public static class PatternPatternNavigationKeyboard
{
	public static bool TryGetDelta(
		char value,
		out int delta)
	{
		delta = value switch
		{
			'+' => 1,
			'-' => -1,
			_ => 0,
		};
		return delta != 0;
	}
}
