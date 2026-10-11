using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.Core.Filters;

/// <summary>A resolved stage in serial processing order, including an
/// independent parameter snapshot (not mutable DSP processing state).</summary>
public sealed record ConfiguredFilterStage(
	int StageId,
	string Name,
	FilterKind Kind,
	FilterSpecificationInstance Parameters);

/// <summary>
/// One configured use of a shared Filter definition. Explicit per-stage
/// overrides are keyed by immutable stage identity, never by display name or
/// ordinal. Renames and reorders therefore preserve parameters, while
/// changing the stage type invalidates the old type's overrides.
/// </summary>
public sealed class FilterInstance
{
	private sealed class StageOverrides
	{
		public required FilterKind Kind { get; init; }
		public Dictionary<string, FilterParameterValue> Values { get; init; } =
			new(StringComparer.OrdinalIgnoreCase);

		public StageOverrides Clone()
			=> new()
			{
				Kind = Kind,
				Values = new Dictionary<string, FilterParameterValue>(
					Values, StringComparer.OrdinalIgnoreCase),
			};
	}

	private readonly FilterDefinition _definition;
	private readonly Dictionary<int, StageOverrides> _overrides = [];

	public FilterInstance(FilterDefinition definition)
	{
		_definition = definition
			?? throw new ArgumentNullException(nameof(definition));
	}

	private FilterInstance(FilterInstance other)
	{
		_definition = other._definition;
		foreach ((int id, StageOverrides overrides) in other._overrides)
			_overrides.Add(id, overrides.Clone());
	}

	public ObjectId DefinitionId => _definition.Id;

	/// <summary>Constructs a new effective snapshot in the definition's
	/// current serial order. Editing the returned snapshot never changes this
	/// FilterInstance, its definition or another consumer's configuration.</summary>
	public IReadOnlyList<ConfiguredFilterStage> GetStages()
	{
		ConfiguredFilterStage[] stages =
			_definition.Stages.Select(stage =>
				new ConfiguredFilterStage(stage.StageId, stage.Name, stage.Kind,
					CreateParameters(stage))).ToArray();
		return Array.AsReadOnly(stages);
	}

	public FilterSpecificationInstance GetStageParameters(int stageId)
		=> CreateParameters(_definition.GetStage(stageId));

	public FilterSpecificationInstance GetStageParameters(string stageName)
		=> CreateParameters(_definition.GetStage(stageName));

	/// <summary>
	/// Atomically validates the complete change before saving overrides.
	/// Stage defaults remain independently editable in the shared definition.
	/// </summary>
	public void SetStageParameters(
		int stageId,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		FilterStageDefinition stage = _definition.GetStage(stageId);
		KeyValuePair<string, FilterParameterValue>[] batch = assignments.ToArray();
		FilterSpecificationInstance candidate = CreateParameters(stage);
		candidate.SetParameters(batch); // validate the whole batch first

		Dictionary<string, FilterParameterValue> values =
			_overrides.TryGetValue(stageId, out StageOverrides? previous)
				&& previous.Kind == stage.Kind
				? new(previous.Values, StringComparer.OrdinalIgnoreCase)
				: new(StringComparer.OrdinalIgnoreCase);

		foreach ((string name, _) in batch)
		{
			FilterParameterDescriptor descriptor = candidate.Parameters
				.First(p => string.Equals(p.Name, name,
					StringComparison.OrdinalIgnoreCase));
			values[descriptor.Name] = candidate.GetParameter(descriptor.Name);
		}

		if (values.Count != 0)
			_overrides[stageId] = new StageOverrides
			{
				Kind = stage.Kind,
				Values = values,
			};
	}

	public void SetStageParameters(
		string stageName,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments)
		=> SetStageParameters(_definition.GetStage(stageName).StageId, assignments);

	public void SetStageParameter(
		int stageId, string parameterName, FilterParameterValue value)
		=> SetStageParameters(stageId,
			new[] { new KeyValuePair<string, FilterParameterValue>(
				parameterName, value) });

	/// <summary>Returns only explicit overrides, not inherited defaults.
	/// A stale stage or a changed filter type yields no active overrides.</summary>
	public IReadOnlyDictionary<string, FilterParameterValue> GetOverrides(
		int stageId)
	{
		FilterStageDefinition stage = _definition.GetStage(stageId);
		if (!_overrides.TryGetValue(stageId, out StageOverrides? overrides)
			|| overrides.Kind != stage.Kind)
			return new ReadOnlyDictionary<string, FilterParameterValue>(
				new Dictionary<string, FilterParameterValue>(
					StringComparer.OrdinalIgnoreCase));
		return new ReadOnlyDictionary<string, FilterParameterValue>(
			new Dictionary<string, FilterParameterValue>(
				overrides.Values, StringComparer.OrdinalIgnoreCase));
	}

	public FilterInstance Clone() => new(this);

	private FilterSpecificationInstance CreateParameters(FilterStageDefinition stage)
	{
		FilterSpecificationInstance parameters = stage.CreateParameters();
		if (_overrides.TryGetValue(stage.StageId, out StageOverrides? overrides)
			&& overrides.Kind == stage.Kind)
			parameters.SetParameters(overrides.Values);
		return parameters;
	}
}

/// <summary>
/// Ordered, independently owned configured layers. Composition of nested
/// Instruments adds the inner layers first, followed by the outer layers;
/// copying configurations must never share mutable parameter overrides.
/// </summary>
public sealed class FilterChain
{
	private readonly ReadOnlyCollection<FilterInstance> _layers;

	public FilterChain(IEnumerable<FilterInstance> layers)
	{
		ArgumentNullException.ThrowIfNull(layers);
		List<FilterInstance> copied = [];
		foreach (FilterInstance layer in layers)
		{
			ArgumentNullException.ThrowIfNull(layer);
			copied.Add(layer.Clone());
		}
		_layers = copied.AsReadOnly();
	}

	public int Count => _layers.Count;

	/// <summary>Returns independent copies so consumers cannot mutate an
	/// existing chain by editing an exposed layer.</summary>
	public IReadOnlyList<FilterInstance> Layers
		=> Array.AsReadOnly(_layers.Select(layer => layer.Clone()).ToArray());

	public static FilterChain Empty => new(Array.Empty<FilterInstance>());

	public FilterChain Append(FilterInstance outer)
	{
		ArgumentNullException.ThrowIfNull(outer);
		return new FilterChain(_layers.Concat(new[] { outer }));
	}

	public static FilterChain Compose(FilterChain inner, FilterChain outer)
	{
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(outer);
		return new FilterChain(inner._layers.Concat(outer._layers));
	}
}
