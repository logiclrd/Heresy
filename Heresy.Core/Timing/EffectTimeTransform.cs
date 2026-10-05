using System;

namespace Heresy.Core.Timing;

/// <summary>
/// Converts wall time to the local time domain seen by one effect invocation.
/// The local rate is exactly one second per wall second at the invocation's
/// starting tempo, and thereafter follows the ratio of current tempo to that
/// captured reference tempo.
/// </summary>
public sealed class EffectTimeTransform
{
	private readonly TrackerTimeMap _timeMap;
	private readonly double _startTimeSeconds;
	private readonly double _startTick;

	public EffectTimeTransform(
		TrackerTimeMap timeMap,
		double startTimeSeconds)
	{
		_timeMap = timeMap
			?? throw new ArgumentNullException(nameof(timeMap));

		if (double.IsNaN(startTimeSeconds)
			|| double.IsInfinity(startTimeSeconds)
			|| startTimeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(startTimeSeconds));
		}

		_startTimeSeconds = startTimeSeconds;
		_startTick =
			timeMap.GetTickAtTime(startTimeSeconds);
		ReferenceTempo =
			timeMap.GetTempoAtTime(startTimeSeconds);
	}

	public double ReferenceTempo { get; }

	public double ReferenceTickDurationSeconds =>
		SequencingConstants.Diachron.TotalSeconds
			/ ReferenceTempo;

	public double GetTimeSeconds(double wallTimeSeconds)
	{
		if (double.IsNaN(wallTimeSeconds)
			|| double.IsInfinity(wallTimeSeconds)
			|| wallTimeSeconds < _startTimeSeconds)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallTimeSeconds));
		}

		double elapsedTicks =
			_timeMap.GetTickAtTime(wallTimeSeconds)
				- _startTick;

		return elapsedTicks
			* ReferenceTickDurationSeconds;
	}

	public double GetRate(double wallTimeSeconds)
	{
		if (double.IsNaN(wallTimeSeconds)
			|| double.IsInfinity(wallTimeSeconds)
			|| wallTimeSeconds < _startTimeSeconds)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallTimeSeconds));
		}

		return _timeMap.GetTempoAtTime(wallTimeSeconds)
			/ ReferenceTempo;
	}
}
