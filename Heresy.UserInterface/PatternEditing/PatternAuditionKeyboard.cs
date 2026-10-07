using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternAuditionKind
{
	Note,
	Row,
}

public static class PatternAuditionKeyboard
{
	public static bool TryGetKind(
		PhysicalKey key,
		out PatternAuditionKind kind)
	{
		switch (key)
		{
			case PhysicalKey.Digit4:
				kind = PatternAuditionKind.Note;
				return true;

			case PhysicalKey.Digit8:
				kind = PatternAuditionKind.Row;
				return true;

			default:
				kind = default;
				return false;
		}
	}
}
