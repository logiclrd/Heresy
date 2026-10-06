using System;

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
}
