using System;

using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Impulse-Tracker-style computer-keyboard piano mapping. Offsets are measured
/// in semitones from C of the selected base octave.
/// </summary>
public static class PatternNoteKeyboard
{
	public static bool TryGetSemitoneOffset(
		char key,
		out int semitoneOffset)
	{
		char value = char.ToUpperInvariant(key);
		semitoneOffset = value switch
		{
			'Z' => 0,
			'S' => 1,
			'X' => 2,
			'D' => 3,
			'C' => 4,
			'V' => 5,
			'G' => 6,
			'B' => 7,
			'H' => 8,
			'N' => 9,
			'J' => 10,
			'M' => 11,

			'Q' => 12,
			'2' => 13,
			'W' => 14,
			'3' => 15,
			'E' => 16,
			'R' => 17,
			'5' => 18,
			'T' => 19,
			'6' => 20,
			'Y' => 21,
			'7' => 22,
			'U' => 23,

			'I' => 24,
			'9' => 25,
			'O' => 26,
			'0' => 27,
			'P' => 28,
			_ => -1,
		};
		return semitoneOffset >= 0;
	}

	public static bool TryGetSemitoneOffset(
		PhysicalKey key,
		out int semitoneOffset)
	{
		if (!TryGetTrackerCharacter(key, out char trackerKey))
		{
			semitoneOffset = -1;
			return false;
		}

		return TryGetSemitoneOffset(trackerKey, out semitoneOffset);
	}

	/// <summary>
	/// Maps a layout-independent physical key to the US-QWERTY character used
	/// by the tracker piano. This deliberately ignores the active OS keyboard
	/// layout and the text symbol produced by the key press.
	/// </summary>
	public static bool TryGetTrackerCharacter(
		PhysicalKey key,
		out char trackerKey)
	{
		trackerKey = key switch
		{
			PhysicalKey.Z => 'Z',
			PhysicalKey.S => 'S',
			PhysicalKey.X => 'X',
			PhysicalKey.D => 'D',
			PhysicalKey.C => 'C',
			PhysicalKey.V => 'V',
			PhysicalKey.G => 'G',
			PhysicalKey.B => 'B',
			PhysicalKey.H => 'H',
			PhysicalKey.N => 'N',
			PhysicalKey.J => 'J',
			PhysicalKey.M => 'M',
			PhysicalKey.Q => 'Q',
			PhysicalKey.Digit2 => '2',
			PhysicalKey.W => 'W',
			PhysicalKey.Digit3 => '3',
			PhysicalKey.E => 'E',
			PhysicalKey.R => 'R',
			PhysicalKey.Digit5 => '5',
			PhysicalKey.T => 'T',
			PhysicalKey.Digit6 => '6',
			PhysicalKey.Y => 'Y',
			PhysicalKey.Digit7 => '7',
			PhysicalKey.U => 'U',
			PhysicalKey.I => 'I',
			PhysicalKey.Digit9 => '9',
			PhysicalKey.O => 'O',
			PhysicalKey.Digit0 => '0',
			PhysicalKey.P => 'P',
			PhysicalKey.Digit1 => '1',
			PhysicalKey.Backquote => '`',
			_ => '\0',
		};
		return trackerKey != '\0';
	}

}
