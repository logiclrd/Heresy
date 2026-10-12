using System;
using System.Numerics;
using System.Collections.Generic;
using Heresy.Core.Filters;

namespace Heresy.Render.Configuration;

/// <summary>
/// Configuration for one physical output stream/speaker.
/// </summary>
public sealed record OutputChannelConfiguration
{
	public OutputChannelConfiguration(
		Vector3 position,
		double positionalImportance = 1.0,
		OutputFilterType filterType = OutputFilterType.None,
		double? cutoffHz = null,
        FilterInstance? filter = null)
	{
		if (!IsFinite(position))
			throw new ArgumentOutOfRangeException(nameof(position));
		if (double.IsNaN(positionalImportance)
			|| double.IsInfinity(positionalImportance)
			|| positionalImportance < 0.0)
		{
			throw new ArgumentOutOfRangeException(nameof(positionalImportance));
		}

		if (filterType is OutputFilterType.LowPass or OutputFilterType.HighPass)
		{
			if (!cutoffHz.HasValue
				|| !(cutoffHz.Value > 0.0)
				|| double.IsNaN(cutoffHz.Value)
				|| double.IsInfinity(cutoffHz.Value))
			{
				throw new ArgumentOutOfRangeException(nameof(cutoffHz));
			}
		}

        if (filterType == OutputFilterType.Chain && filter is null)
            throw new ArgumentException("A reusable speaker Filter chain must be selected.", nameof(filter));
        if (filterType is < OutputFilterType.None or > OutputFilterType.Chain)
            throw new ArgumentOutOfRangeException(nameof(filterType));
        if (filterType != OutputFilterType.Chain && filter is not null)
            throw new ArgumentException("Only a reusable speaker Filter can own an inline chain.", nameof(filter));
        _filter = filter is null ? null : Freeze(filter);
		Position = position;
		PositionalImportance = positionalImportance;
		FilterType = filterType;
		CutoffHz = cutoffHz;
	}

    private readonly FilterInstance? _filter;
    /// <summary>A private inline snapshot, never a mutable SongDocument Filter
    /// reference. Each processing session prepares its own DSP state.</summary>
    public FilterInstance? Filter => _filter?.Clone();

	public Vector3 Position { get; }
	public double PositionalImportance { get; }
	public OutputFilterType FilterType { get; }
	public double? CutoffHz { get; }

    /// <summary>Rebuild the definition from complete effective stage values,
    /// so edits to a song's FilterDefinition cannot mutate saved audio-device
    /// preferences or an already-started render snapshot.</summary>
    private static FilterInstance Freeze(FilterInstance filter)
    {
        List<FilterStageDefinition> stages = [];
        foreach (ConfiguredFilterStage stage in filter.GetStages())
        {
            List<KeyValuePair<string, FilterParameterValue>> values = [];
            foreach (FilterParameterDescriptor descriptor in stage.Parameters.Parameters)
                values.Add(new(descriptor.Name,
                    stage.Parameters.GetParameter(descriptor.Name)));
            stages.Add(new FilterStageDefinition(stage.StageId, stage.Name,
                stage.Kind, values));
        }
        return new FilterInstance(new FilterDefinition(filter.DefinitionId,
            filter.Definition.Name, stages));
    }

	private static bool IsFinite(Vector3 value)
		=> float.IsFinite(value.X)
			&& float.IsFinite(value.Y)
			&& float.IsFinite(value.Z);
}
