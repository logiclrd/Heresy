using System;

using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Layout-independent tracker octave shortcuts. Modifier combinations used by
/// higher-level tracker commands are deliberately left unhandled.
/// </summary>
public static class PatternOctaveKeyboard
{
	public static bool TryAdjust(
		PhysicalKey key,
		KeyModifiers modifiers,
		int currentOctave,
		out int octave)
	{
		if (currentOctave is < 0 or > 8)
			throw new ArgumentOutOfRangeException(nameof(currentOctave));

		octave = currentOctave;

		KeyModifiers blockingModifiers =
			KeyModifiers.Control
				| KeyModifiers.Alt
				| KeyModifiers.Meta;
		if ((modifiers & blockingModifiers) != 0)
			return false;

		switch (key)
		{
			case PhysicalKey.NumPadMultiply:
				octave = Math.Min(8, currentOctave + 1);
				return true;

			case PhysicalKey.NumPadDivide:
				octave = Math.Max(0, currentOctave - 1);
				return true;

			default:
				return false;
		}
	}
}
