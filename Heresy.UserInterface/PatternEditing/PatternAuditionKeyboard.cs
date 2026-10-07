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
		kind = default;
		return false;
	}
}
