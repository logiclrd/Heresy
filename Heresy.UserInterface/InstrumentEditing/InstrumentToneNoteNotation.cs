using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// Musical names are always measured against the nearest of the twelve
/// chromatic semitones, while +/- suffixes count *individual instrument
/// tone-index steps*. No fixed 48-division assumption is made.
/// C-4 is log2(pitch)=0, the same reference as InstrumentSound.
/// </summary>
public static class InstrumentToneNoteNotation
{
	private static readonly string[] Names =
		["C", "C#", "D", "D#", "E", "F", "F#", "G",
		 "G#", "A", "A#", "B"];

	public static string Format(int index, double divisions, int offset)
	{
		ValidateDivisions(divisions);
		double semitones = 12.0 * ((double)index - offset) / divisions;
		if (!double.IsFinite(semitones)
			|| semitones <= int.MinValue + 48.0
			|| semitones >= int.MaxValue - 48.0)
			throw new ArgumentOutOfRangeException(nameof(index));

		// Chromatic ties favor the lower note: C++ rather than C#--.
		int nearest = checked((int)Math.Ceiling(semitones - 0.5));
		int nominal = checked((int)Math.Round(
			offset + nearest * divisions / 12.0,
			MidpointRounding.AwayFromZero));
		int steps = checked(index - nominal);
		int note = ((nearest % 12) + 12) % 12;
		int octave = 4 + (int)Math.Floor(nearest / 12.0);
		string suffix = steps > 0 ? new string('+', steps)
			: steps < 0 ? new string('-', checked(-steps))
			: "";
		return $"{Names[note]}-{octave}{suffix}";
	}

	/// <summary>The editable Pitch column stores log2(multiplier).</summary>
	public static double LogarithmicOffset(double multiplier)
	{
		if (!(multiplier > 0) || !double.IsFinite(multiplier))
			throw new ArgumentOutOfRangeException(nameof(multiplier));
		return Math.Log2(multiplier);
	}

	public static double MultiplierFromOffset(double offset)
	{
		if (!double.IsFinite(offset))
			throw new ArgumentOutOfRangeException(nameof(offset));
		double multiplier = Math.Pow(2.0, offset);
		if (!(multiplier > 0) || !double.IsFinite(multiplier))
			throw new ArgumentOutOfRangeException(nameof(offset));
		return multiplier;
	}

	/// <summary>
	/// Offer nearby instrument pitch-index positions within +/-0.3 octaves
	/// (i.e., +/-0.3 in the logarithmic offset column) of the entered pitch.
	/// The options represent exact multipliers, while *automatic* nearest
	/// selection never alters the entered multiplier.
	/// </summary>
	public static IReadOnlyList<InstrumentToneNoteChoice> Options(
		int nominalIndex, double divisions, int offset,
		double logarithmicOffset)
	{
		ValidateDivisions(divisions);
		if (!double.IsFinite(logarithmicOffset))
			throw new ArgumentOutOfRangeException(nameof(logarithmicOffset));
		double center = nominalIndex + logarithmicOffset * divisions;
		double radius = 0.3 * divisions;
		if (!double.IsFinite(center) || !double.IsFinite(radius)
			|| center - radius < 0 || center + radius > int.MaxValue - 1)
		{
			// Pitch multipliers cannot resolve a negative Core table index.
			// Entirely negative regions are simply empty.
			if (center + radius < 0)
				return [];
			if (!double.IsFinite(center) || !double.IsFinite(radius)
				|| center + radius > int.MaxValue - 1)
				throw new ArgumentOutOfRangeException(nameof(logarithmicOffset));
		}
		int start = (int)Math.Max(0, Math.Ceiling(center - radius - 1e-10));
		int end = (int)Math.Min(int.MaxValue - 1,
			Math.Floor(center + radius + 1e-10));
		if (end < start)
			return [];
		if ((long)end - start >= 4096)
			throw new InvalidOperationException(
				"Pitch resolution produces more than 4096 nearby note choices.");
		List<InstrumentToneNoteChoice> result = [];
		for (int candidate = start; candidate <= end; candidate++)
		{
			double value = (candidate - (double)nominalIndex) / divisions;
			result.Add(new InstrumentToneNoteChoice(
				candidate, Format(candidate, divisions, offset),
				value, MultiplierFromOffset(value)));
		}
		return result;
	}

	public static InstrumentToneNoteChoice? Nearest(
		IReadOnlyList<InstrumentToneNoteChoice> choices,
		double logarithmicOffset)
	{
		ArgumentNullException.ThrowIfNull(choices);
		InstrumentToneNoteChoice? best = null;
		double bestDistance = double.PositiveInfinity;
		foreach (InstrumentToneNoteChoice choice in choices)
		{
			double distance = Math.Abs(
				logarithmicOffset - choice.LogarithmicOffset);
			// Ties favor the lower pitch (choices are ascending).
			if (distance < bestDistance - 1e-12)
			{
				best = choice;
				bestDistance = distance;
			}
		}
		return best;
	}

	private static void ValidateDivisions(double divisions)
	{
		if (!(divisions > 0) || !double.IsFinite(divisions))
			throw new ArgumentOutOfRangeException(nameof(divisions));
	}
}

public sealed record InstrumentToneNoteChoice(
	int Index, string Name, double LogarithmicOffset,
	double Multiplier)
{
	public override string ToString() => Name;
}
