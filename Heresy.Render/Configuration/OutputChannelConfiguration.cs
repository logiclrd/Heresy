using System;
using System.Numerics;

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
		double? cutoffHz = null)
	{
		if (!IsFinite(position))
			throw new ArgumentOutOfRangeException(nameof(position));
		if (double.IsNaN(positionalImportance)
			|| double.IsInfinity(positionalImportance)
			|| positionalImportance < 0.0)
		{
			throw new ArgumentOutOfRangeException(nameof(positionalImportance));
		}

		if (filterType != OutputFilterType.None)
		{
			if (!cutoffHz.HasValue
				|| !(cutoffHz.Value > 0.0)
				|| double.IsNaN(cutoffHz.Value)
				|| double.IsInfinity(cutoffHz.Value))
			{
				throw new ArgumentOutOfRangeException(nameof(cutoffHz));
			}
		}

		Position = position;
		PositionalImportance = positionalImportance;
		FilterType = filterType;
		CutoffHz = cutoffHz;
	}

	public Vector3 Position { get; }
	public double PositionalImportance { get; }
	public OutputFilterType FilterType { get; }
	public double? CutoffHz { get; }

	private static bool IsFinite(Vector3 value)
		=> float.IsFinite(value.X)
			&& float.IsFinite(value.Y)
			&& float.IsFinite(value.Z);
}
