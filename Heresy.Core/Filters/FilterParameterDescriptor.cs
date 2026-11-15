using System;
using System.Collections.Generic;

namespace Heresy.Core.Filters;

public enum FilterParameterValueKind
{
	Number,
	Choice,
}

/// <summary>
/// One typed value for a filter parameter. The descriptor, not the value
/// itself, is authoritative for range, finiteness and allowed-choice checks.
/// This lets editors validate drafts and entire parameter batches uniformly.
/// </summary>
public sealed record FilterParameterValue
{
	private FilterParameterValue(
		FilterParameterValueKind kind, double numericValue, string? choiceValue)
	{
		Kind = kind;
		NumericValue = numericValue;
		ChoiceValue = choiceValue;
	}

	public FilterParameterValueKind Kind { get; }
	public double NumericValue { get; }
	public string? ChoiceValue { get; }

	public static FilterParameterValue FromNumber(double value)
		=> new(FilterParameterValueKind.Number, value, null);

	public static FilterParameterValue FromChoice(string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		return new(FilterParameterValueKind.Choice, 0, value);
	}
}

/// <summary>
/// Immutable, type-specific parameter schema shared by the instance editor,
/// effect-command validator and eventual DSP preparation. Units and limits
/// describe authored values; sample-rate/Nyquist checks belong to preparation.
/// </summary>
public sealed class FilterParameterDescriptor
{
	private FilterParameterDescriptor(
		string name,
		FilterParameterValueKind kind,
		FilterParameterValue defaultValue,
		double? minimum,
		double? maximum,
		string units,
		bool structural,
		bool modulatable,
		bool integer,
		bool odd,
		IReadOnlyList<string> choices)
	{
		Name = name;
		Kind = kind;
		DefaultValue = defaultValue;
		Minimum = minimum;
		Maximum = maximum;
		Units = units;
		IsStructural = structural;
		IsModulatable = modulatable && kind == FilterParameterValueKind.Number && !structural;
		IsInteger = integer;
		IsOddOnly = odd;
		Choices = choices;
		Normalize(defaultValue);
	}

	public string Name { get; }
	public FilterParameterValueKind Kind { get; }
	public FilterParameterValue DefaultValue { get; }
	public double? Minimum { get; }
	public double? Maximum { get; }
	public string Units { get; }
	public bool IsStructural { get; }
	public bool IsModulatable { get; }
	public bool IsInteger { get; }
	public bool IsOddOnly { get; }
	public IReadOnlyList<string> Choices { get; }

	public static FilterParameterDescriptor Number(
		string name, double defaultValue, double minimum, double maximum,
		string units = "", bool structural = false, bool integer = false,
		bool odd = false, bool fmPort = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (!double.IsFinite(minimum) || !double.IsFinite(maximum)
			|| minimum > maximum)
			throw new ArgumentException("Numeric parameter range must be finite and ordered.");
		if (odd && !integer)
			throw new ArgumentException("Odd-only parameters must be integers.");
		return new FilterParameterDescriptor(name, FilterParameterValueKind.Number,
			FilterParameterValue.FromNumber(defaultValue), minimum, maximum,
			units ?? throw new ArgumentNullException(nameof(units)),
			structural, fmPort, integer, odd, Array.AsReadOnly<string>([]));
	}

	/// <summary>
	/// Choices are not FM graph audio-rate ports, independently of whether
	/// the running processor can migrate their state. Most choices are structural.
	/// </summary>
	public static FilterParameterDescriptor Choice(
		string name, string defaultValue, params string[] choices)
		=> CreateChoice(name, defaultValue, structural: true, choices);

	/// <summary>An output/algorithm selection proven to preserve the existing
	/// runtime state shape. Never creates an audio-rate FM port.</summary>
	public static FilterParameterDescriptor LiveChoice(
		string name, string defaultValue, params string[] choices)
		=> CreateChoice(name, defaultValue, structural: false, choices);

	private static FilterParameterDescriptor CreateChoice(
		string name, string defaultValue, bool structural, string[] choices)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(choices);
		if (choices.Length == 0)
			throw new ArgumentException("At least one choice is required.", nameof(choices));
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string choice in choices)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(choice);
			if (!seen.Add(choice))
				throw new ArgumentException("Duplicate parameter choices are not allowed.",
					nameof(choices));
		}

		return new FilterParameterDescriptor(name, FilterParameterValueKind.Choice,
			FilterParameterValue.FromChoice(defaultValue), null, null, "", structural,
			false, false, false, Array.AsReadOnly((string[])choices.Clone()));
	}

	/// <summary>
	/// Validates and canonicalizes choices using the selected filter type's
	/// vocabulary. No validation mutates the original value or the descriptor.
	/// </summary>
	public FilterParameterValue Normalize(FilterParameterValue value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (Kind != value.Kind)
			throw new ArgumentException($"Parameter {Name} requires {Kind}.", nameof(value));

		if (Kind == FilterParameterValueKind.Choice)
		{
			foreach (string allowed in Choices)
				if (string.Equals(value.ChoiceValue, allowed,
					StringComparison.OrdinalIgnoreCase))
					return FilterParameterValue.FromChoice(allowed);
			throw new ArgumentOutOfRangeException(nameof(value),
				$"Parameter {Name} does not recognize choice '{value.ChoiceValue}'.");
		}

		double number = value.NumericValue;
		if (!double.IsFinite(number) || number < Minimum!.Value
			|| number > Maximum!.Value
			|| (IsInteger && number != Math.Truncate(number))
			|| (IsOddOnly && ((long)number & 1L) == 0L))
		{
			throw new ArgumentOutOfRangeException(nameof(value),
				$"Parameter {Name} has an invalid numeric value.");
		}
		return value;
	}
}
