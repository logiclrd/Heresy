using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.Filters;

/// <summary>
/// One configured occurrence of a selected filter stage, not its DSP history.
/// Get/Set names come exclusively from the enumerated parameter descriptors.
/// </summary>
public interface IFilterSpecificationInstance
{
	FilterKind Kind { get; }
	IReadOnlyList<FilterParameterDescriptor> Parameters { get; }
	FilterParameterValue GetParameter(string name);
	void SetParameter(string name, FilterParameterValue value);
	void SetParameters(IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments);
}

/// <summary>
/// Validated per-use stage parameters. Atomic batch updates allow commands
/// to set interdependent fields (e.g. both FIR band edges) in one operation.
/// Changing a stage's *type* constructs a different specification instance.
/// The later Filter-object milestone will bind instances to stable stage IDs.
/// </summary>
public sealed class FilterSpecificationInstance : IFilterSpecificationInstance
{
	private readonly IReadOnlyDictionary<string, FilterParameterDescriptor> _descriptors;
	private Dictionary<string, FilterParameterValue> _values;

	public FilterSpecificationInstance(FilterKind kind)
	{
		Parameters = FilterCatalogue.GetParameters(kind);
		Kind = kind;
		Dictionary<string, FilterParameterDescriptor> descriptors =
			new(StringComparer.OrdinalIgnoreCase);
		_values = new Dictionary<string, FilterParameterValue>(
			StringComparer.OrdinalIgnoreCase);
		foreach (FilterParameterDescriptor descriptor in Parameters)
		{
			if (!descriptors.TryAdd(descriptor.Name, descriptor))
				throw new InvalidOperationException(
					$"Duplicate filter parameter '{descriptor.Name}'.");
			_values.Add(descriptor.Name, descriptor.Normalize(descriptor.DefaultValue));
		}
		_descriptors = descriptors;
	}

	public FilterKind Kind { get; }
	public IReadOnlyList<FilterParameterDescriptor> Parameters { get; }

	public FilterParameterValue GetParameter(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return _values.TryGetValue(name, out FilterParameterValue? value)
			? value
			: throw new KeyNotFoundException(
				$"Filter {Kind} has no parameter named '{name}'.");
	}

	public void SetParameter(string name, FilterParameterValue value)
		=> SetParameters(new[]
		{
			new KeyValuePair<string, FilterParameterValue>(name, value),
		});

	public void SetParameters(
		IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		Dictionary<string, FilterParameterValue> candidate =
			new(_values, StringComparer.OrdinalIgnoreCase);
		HashSet<string> encountered = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string name, FilterParameterValue value) in assignments)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(name);
			if (!encountered.Add(name))
				throw new ArgumentException(
					$"Duplicate assignment for '{name}'.", nameof(assignments));
			if (!_descriptors.TryGetValue(name,
				out FilterParameterDescriptor? descriptor))
				throw new ArgumentException(
					$"Filter {Kind} has no parameter named '{name}'.",
					nameof(assignments));
			candidate[descriptor.Name] = descriptor.Normalize(value);
		}

		ValidateCombination(candidate);
		_values = candidate;
	}

	private void ValidateCombination(
		IReadOnlyDictionary<string, FilterParameterValue> candidate)
	{
		if (Kind is not (FilterKind.FirBandPass or FilterKind.FirNotch))
			return;

		double lower = candidate["LowCutoffHz"].NumericValue;
		double upper = candidate["HighCutoffHz"].NumericValue;
		if (!(lower < upper))
			throw new ArgumentException(
				"FIR band-pass/notch LowCutoffHz must be below HighCutoffHz.");
	}
}
