using System;
using System.Numerics;
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
        _filter = filter?.Clone();
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

	private static bool IsFinite(Vector3 value)
		=> float.IsFinite(value.X)
			&& float.IsFinite(value.Y)
			&& float.IsFinite(value.Z);
}
