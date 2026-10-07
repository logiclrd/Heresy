using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Recognizes fixed-length tracker row insertion/deletion shortcuts while
/// preserving the established Alt+Insert/Delete effect-stack commands.
/// </summary>
public static class PatternRowMutationKeyboard
{
	public static bool TryGet(
		Key key,
		KeyModifiers modifiers,
		bool effectField,
		out PatternRowMutationKind kind,
		out bool allChannels)
	{
		kind = default;
		allChannels = false;

		if (key is not Key.Insert and not Key.Delete)
			return false;

		KeyModifiers blocked =
			KeyModifiers.Control
				| KeyModifiers.Meta
				| KeyModifiers.Shift;
		if ((modifiers & blocked) != 0)
			return false;

		bool alt =
			(modifiers & KeyModifiers.Alt) != 0;
		if (alt && effectField)
			return false;

		if (modifiers != KeyModifiers.None
			&& modifiers != KeyModifiers.Alt)
		{
			return false;
		}

		kind =
			key == Key.Insert
				? PatternRowMutationKind.Insert
				: PatternRowMutationKind.Delete;
		allChannels = alt;
		return true;
	}
}
