using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Heresy.Core.Filters;

/// <summary>
/// One immutable stage in a reusable Filter definition. StageId, unlike Name,
/// survives renaming and moving stages. Defaults are validated against the
/// selected filter kind and stored independently of caller collections.
/// </summary>
public sealed class FilterStageDefinition
{
	private readonly ReadOnlyDictionary<string, FilterParameterValue> _defaults;

	public FilterStageDefinition(
		int stageId,
		string name,
		FilterKind kind,
		IEnumerable<KeyValuePair<string, FilterParameterValue>>? defaults = null)
	{
		if (stageId <= 0)
			throw new ArgumentOutOfRangeException(nameof(stageId));
		ValidateName(name);

		FilterSpecificationInstance parameters = new(kind);
		if (defaults is not null)
			parameters.SetParameters(defaults);

		StageId = stageId;
		Name = name;
		Kind = kind;

		Dictionary<string, FilterParameterValue> values =
			new(StringComparer.OrdinalIgnoreCase);
		foreach (FilterParameterDescriptor descriptor in parameters.Parameters)
			values.Add(descriptor.Name, parameters.GetParameter(descriptor.Name));
		_defaults = new ReadOnlyDictionary<string, FilterParameterValue>(values);
	}

	public int StageId { get; }
	public string Name { get; }
	public FilterKind Kind { get; }
	public IReadOnlyDictionary<string, FilterParameterValue> DefaultParameters => _defaults;

	/// <summary>Returns a separate mutable parameter set initialized from
	/// these immutable definition defaults; DSP histories are not involved.</summary>
	public FilterSpecificationInstance CreateParameters()
	{
		FilterSpecificationInstance instance = new(Kind);
		instance.SetParameters(_defaults);
		return instance;
	}

	public FilterStageDefinition Rename(string name)
		=> new(StageId, name, Kind, _defaults);

	public FilterStageDefinition ChangeKind(FilterKind kind)
		=> new(StageId, Name, kind);

	public FilterStageDefinition WithDefaults(
		IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments)
	{
		FilterSpecificationInstance candidate = CreateParameters();
		candidate.SetParameters(assignments);
		Dictionary<string, FilterParameterValue> values =
			new(StringComparer.OrdinalIgnoreCase);
		foreach (FilterParameterDescriptor descriptor in candidate.Parameters)
			values.Add(descriptor.Name, candidate.GetParameter(descriptor.Name));
		return new FilterStageDefinition(StageId, Name, Kind, values);
	}

	public static void ValidateName(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		// The stage name participates in textual stageName.parameterName paths.
		// Restrict it to unambiguous ASCII identifiers instead of requiring
		// an escaping syntax in effects, FM inputs or scripts.
		if (!IsStart(name[0]))
			throw new ArgumentException(
				"Stage names must start with an ASCII letter or underscore.",
				nameof(name));
		for (int index = 1; index < name.Length; index++)
			if (!IsStart(name[index])
				&& !(name[index] >= '0' && name[index] <= '9'))
				throw new ArgumentException(
					"Stage names may contain only ASCII letters, digits or underscores.",
					nameof(name));

		static bool IsStart(char c)
			=> (c >= 'A' && c <= 'Z')
				|| (c >= 'a' && c <= 'z')
				|| c == '_';
	}
}
