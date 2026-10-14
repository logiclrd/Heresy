using System;

using Avalonia.Input;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// The octave is an editor-only focus stop, not a new serialized Note field.
/// Numeric input transposes an existing pitched Note by whole octaves and
/// preserves its pitch-class/fine tuning and every other cell property.
/// </summary>
public static class PatternOctaveKeyboardEditor
{
	public static bool TryGetDigit(PhysicalKey key, KeyModifiers modifiers,
		out int octave)
	{
		octave = key switch
		{
			PhysicalKey.Digit0 or PhysicalKey.NumPad0 => 0,
			PhysicalKey.Digit1 or PhysicalKey.NumPad1 => 1,
			PhysicalKey.Digit2 or PhysicalKey.NumPad2 => 2,
			PhysicalKey.Digit3 or PhysicalKey.NumPad3 => 3,
			PhysicalKey.Digit4 or PhysicalKey.NumPad4 => 4,
			PhysicalKey.Digit5 or PhysicalKey.NumPad5 => 5,
			PhysicalKey.Digit6 or PhysicalKey.NumPad6 => 6,
			PhysicalKey.Digit7 or PhysicalKey.NumPad7 => 7,
			PhysicalKey.Digit8 or PhysicalKey.NumPad8 => 8,
			PhysicalKey.Digit9 or PhysicalKey.NumPad9 => 9,
			_ => -1,
		};
		return modifiers == KeyModifiers.None && octave >= 0;
	}

	public static bool Type(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		int octave,
		int skipRows)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);
		if (octave is < 0 or > 9)
			throw new ArgumentOutOfRangeException(nameof(octave));
		if (skipRows is < 0 or > 128)
			throw new ArgumentOutOfRangeException(nameof(skipRows));
		if (cursor.Field != PatternCellField.Octave)
			return false;

		bool changed = false;
		if (pattern.Grid[cursor.Row, cursor.Channel]?.Note
			is StartPatternNote existing)
		{
			// C4 is multiplier 1. Even non-semitone pitches preserve their
			// exact interval within an octave under this transformation.
			double currentOctave = Math.Floor(
				4.0 + Math.Log2(existing.PitchMultiplier));
			if (currentOctave != octave)
			{
				double pitch = existing.PitchMultiplier
					* Math.Pow(2.0, octave - currentOctave);
				if (double.IsFinite(pitch) && pitch > 0.0)
				{
					PatternDocumentEditor.SetNote(workspace, pattern,
						cursor.Row, cursor.Channel,
						new StartPatternNote(existing.SourceId, pitch,
							existing.PlaybackSpeedMultiplier, existing.Mixdown,
							existing.EventFilterDefinitionId));
					changed = true;
				}
			}
		}

		// An accepted digit advances even on an empty Note or a no-op pitch;
		// neither situation fabricates a cell or records an audio revision.
		cursor.AdvanceDown(pattern.RowCount, skipRows);
		return changed;
	}
}
