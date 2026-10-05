using System;

namespace Heresy.Core.Timing;

/// <summary>
/// Continuous tracker-row timing. Row time always traverses [0, Speed] while
/// wall-clock rate follows the effective tempo, which is linear in row time.
/// </summary>
public sealed class TrackerRowTiming
{
	private readonly double _tempoDelta;

	public TrackerRowTiming(
		double speed,
		double startingTempo,
		double endingTempo)
	{
		if (!(speed > 0.0)
			|| double.IsNaN(speed)
			|| double.IsInfinity(speed))
		{
			throw new ArgumentOutOfRangeException(nameof(speed));
		}
		ValidateTempo(startingTempo, nameof(startingTempo));
		ValidateTempo(endingTempo, nameof(endingTempo));

		Speed = speed;
		StartingTempo = startingTempo;
		EndingTempo = endingTempo;
		_tempoDelta = endingTempo - startingTempo;
		RowDurationSeconds = GetWallTimeSeconds(speed);
	}

	public double Speed { get; }
	public double StartingTempo { get; }
	public double EndingTempo { get; }
	public double RowDurationSeconds { get; }

	public double GetTempo(double rowTime)
	{
		ValidateRowTime(rowTime);
		return StartingTempo
			+ _tempoDelta * rowTime / Speed;
	}

	public double GetWallTimeSeconds(double rowTime)
	{
		ValidateRowTime(rowTime);

		if (Math.Abs(_tempoDelta)
			<= Math.Max(
				Math.Abs(StartingTempo),
				Math.Abs(EndingTempo))
				* 1e-12)
		{
			return SequencingConstants.Diachron.TotalSeconds
				* rowTime
				/ StartingTempo;
		}

		double tempo = GetTempo(rowTime);
		return SequencingConstants.Diachron.TotalSeconds
			* Speed
			/ _tempoDelta
			* Math.Log(tempo / StartingTempo);
	}

	public double GetRowTime(double wallTimeSeconds)
	{
		if (double.IsNaN(wallTimeSeconds)
			|| double.IsInfinity(wallTimeSeconds)
			|| wallTimeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallTimeSeconds));
		}

		if (wallTimeSeconds >= RowDurationSeconds)
			return Speed;

		if (Math.Abs(_tempoDelta)
			<= Math.Max(
				Math.Abs(StartingTempo),
				Math.Abs(EndingTempo))
				* 1e-12)
		{
			return wallTimeSeconds
				* StartingTempo
				/ SequencingConstants.Diachron.TotalSeconds;
		}

		double slope = _tempoDelta / Speed;
		double exponent =
			slope
				* wallTimeSeconds
				/ SequencingConstants.Diachron.TotalSeconds;

		return Math.Clamp(
			StartingTempo / slope * Expm1(exponent),
			0.0,
			Speed);
	}

	public double GetRowTimeRate(double wallTimeSeconds)
		=> GetTempo(GetRowTime(wallTimeSeconds))
			/ SequencingConstants.Diachron.TotalSeconds;

	private void ValidateRowTime(double rowTime)
	{
		if (double.IsNaN(rowTime)
			|| double.IsInfinity(rowTime)
			|| rowTime < 0.0
			|| rowTime > Speed)
		{
			throw new ArgumentOutOfRangeException(nameof(rowTime));
		}
	}

	private static void ValidateTempo(
		double tempo,
		string parameterName)
	{
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}

	private static double Expm1(double value)
	{
		if (Math.Abs(value) > 1e-5)
			return Math.Exp(value) - 1.0;

		double value2 = value * value;
		double value3 = value2 * value;
		double value4 = value3 * value;
		return value
			+ value2 / 2.0
			+ value3 / 6.0
			+ value4 / 24.0;
	}
}
