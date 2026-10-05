using System;
using System.Collections.Generic;

namespace Heresy.Core.Timing;

/// <summary>
/// Monotonic mapping between wall-clock seconds and continuous tracker-tick
/// position. Tempo is measured in ticks per diachron. Constant segments are
/// linear in both domains; ramp segments are linear in tracker position, so
/// wall time is their analytic integral.
/// </summary>
public sealed class TrackerTimeMap
{
	private readonly record struct Segment(
		double StartTimeSeconds,
		double EndTimeSeconds,
		double StartTick,
		double EndTick,
		double StartTempo,
		double EndTempo)
	{
		public double TickSpan => EndTick - StartTick;
		public double TempoDelta => EndTempo - StartTempo;
	}

	private readonly List<Segment> _segments = [];
	private double _timeSummationCompensation;
	private double _currentTimeSeconds;
	private double _currentTick;
	private double _currentTempo;

	public TrackerTimeMap(
		double initialTempo = SequencingConstants.DefaultTempo)
	{
		ValidateTempo(initialTempo, nameof(initialTempo));
		_currentTempo = initialTempo;
	}

	public double CurrentTimeSeconds => _currentTimeSeconds;
	public double CurrentTick => _currentTick;
	public double CurrentTempo => _currentTempo;

	public void SetTempo(double tempo)
	{
		ValidateTempo(tempo, nameof(tempo));
		_currentTempo = tempo;
	}

	public void AppendConstantTime(double seconds)
	{
		if (double.IsNaN(seconds)
			|| double.IsInfinity(seconds)
			|| seconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(nameof(seconds));
		}

		if (seconds == 0.0)
			return;

		double ticks =
			seconds
				* _currentTempo
				/ SequencingConstants.Diachron.TotalSeconds;

		AppendSegment(
			_currentTempo,
			ticks,
			seconds);
	}

	public void AppendConstantTicks(double trackerTicks)
	{
		ValidateTickSpan(trackerTicks);

		double seconds =
			SequencingConstants.Diachron.TotalSeconds
				* trackerTicks
				/ _currentTempo;

		AppendSegment(
			_currentTempo,
			trackerTicks,
			seconds);
	}

	public void AppendTempoRamp(
		double endingTempo,
		double trackerTicks)
	{
		ValidateTempo(endingTempo, nameof(endingTempo));
		ValidateTickSpan(trackerTicks);

		double startingTempo = _currentTempo;
		double delta = endingTempo - startingTempo;
		double seconds;

		if (Math.Abs(delta)
			<= Math.Max(
				Math.Abs(startingTempo),
				Math.Abs(endingTempo))
				* 1e-12)
		{
			seconds =
				SequencingConstants.Diachron.TotalSeconds
					* trackerTicks
					/ startingTempo;
		}
		else
		{
			seconds =
				SequencingConstants.Diachron.TotalSeconds
					* trackerTicks
					/ delta
					* Math.Log(
						endingTempo / startingTempo);
		}

		AppendSegment(
			endingTempo,
			trackerTicks,
			seconds);
	}

	public void AdvanceToTime(double timeSeconds)
	{
		if (double.IsNaN(timeSeconds)
			|| double.IsInfinity(timeSeconds)
			|| timeSeconds < _currentTimeSeconds - 1e-12)
		{
			throw new ArgumentOutOfRangeException(
				nameof(timeSeconds));
		}

		double delta =
			timeSeconds - _currentTimeSeconds;
		if (delta > 0.0)
			AppendConstantTime(delta);
	}

	public double GetTickAtTime(double timeSeconds)
	{
		if (double.IsNaN(timeSeconds)
			|| double.IsInfinity(timeSeconds)
			|| timeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(timeSeconds));
		}

		if (timeSeconds >= _currentTimeSeconds)
		{
			return _currentTick
				+ (timeSeconds - _currentTimeSeconds)
					* _currentTempo
					/ SequencingConstants.Diachron.TotalSeconds;
		}

		Segment segment =
			FindSegmentByTime(timeSeconds);
		double localTime =
			timeSeconds - segment.StartTimeSeconds;

		if (Math.Abs(segment.TempoDelta)
			<= Math.Max(
				Math.Abs(segment.StartTempo),
				Math.Abs(segment.EndTempo))
				* 1e-12)
		{
			return segment.StartTick
				+ localTime
					* segment.StartTempo
					/ SequencingConstants.Diachron.TotalSeconds;
		}

		double slope =
			segment.TempoDelta / segment.TickSpan;
		double exponent =
			slope
				* localTime
				/ SequencingConstants.Diachron.TotalSeconds;
		double localTicks =
			segment.StartTempo / slope
				* Expm1(exponent);

		return Math.Clamp(
			segment.StartTick + localTicks,
			segment.StartTick,
			segment.EndTick);
	}

	public double GetTimeAtTick(double trackerTick)
	{
		if (double.IsNaN(trackerTick)
			|| double.IsInfinity(trackerTick)
			|| trackerTick < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerTick));
		}

		if (trackerTick >= _currentTick)
		{
			return _currentTimeSeconds
				+ (trackerTick - _currentTick)
					* SequencingConstants.Diachron.TotalSeconds
					/ _currentTempo;
		}

		Segment segment =
			FindSegmentByTick(trackerTick);
		double localTicks =
			trackerTick - segment.StartTick;

		if (Math.Abs(segment.TempoDelta)
			<= Math.Max(
				Math.Abs(segment.StartTempo),
				Math.Abs(segment.EndTempo))
				* 1e-12)
		{
			return segment.StartTimeSeconds
				+ localTicks
					* SequencingConstants.Diachron.TotalSeconds
					/ segment.StartTempo;
		}

		double slope =
			segment.TempoDelta / segment.TickSpan;
		double tempo =
			segment.StartTempo + slope * localTicks;

		return segment.StartTimeSeconds
			+ SequencingConstants.Diachron.TotalSeconds
				/ slope
				* Math.Log(
					tempo / segment.StartTempo);
	}

	public double GetTempoAtTime(double timeSeconds)
	{
		if (double.IsNaN(timeSeconds)
			|| double.IsInfinity(timeSeconds)
			|| timeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(timeSeconds));
		}

		if (timeSeconds >= _currentTimeSeconds)
			return _currentTempo;

		Segment segment =
			FindSegmentByTime(timeSeconds);
		double tick = GetTickAtTime(timeSeconds);
		return TempoAtTickInSegment(segment, tick);
	}

	public double GetTempoAtTick(double trackerTick)
	{
		if (double.IsNaN(trackerTick)
			|| double.IsInfinity(trackerTick)
			|| trackerTick < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerTick));
		}

		if (trackerTick >= _currentTick)
			return _currentTempo;

		Segment segment =
			FindSegmentByTick(trackerTick);
		return TempoAtTickInSegment(
			segment,
			trackerTick);
	}

	private void AppendSegment(
		double endingTempo,
		double trackerTicks,
		double seconds)
	{
		double adjustedSeconds =
			seconds - _timeSummationCompensation;
		double endTime =
			_currentTimeSeconds + adjustedSeconds;
		_timeSummationCompensation =
			(endTime - _currentTimeSeconds)
				- adjustedSeconds;

		double endTick =
			_currentTick + trackerTicks;

		_segments.Add(
			new Segment(
				_currentTimeSeconds,
				endTime,
				_currentTick,
				endTick,
				_currentTempo,
				endingTempo));

		_currentTimeSeconds = endTime;
		_currentTick = endTick;
		_currentTempo = endingTempo;
	}

	private Segment FindSegmentByTime(double timeSeconds)
	{
		int low = 0;
		int high = _segments.Count - 1;

		while (low < high)
		{
			int mid =
				low + (high - low + 1) / 2;
			if (_segments[mid].StartTimeSeconds
				<= timeSeconds)
			{
				low = mid;
			}
			else
			{
				high = mid - 1;
			}
		}

		return _segments[low];
	}

	private Segment FindSegmentByTick(double trackerTick)
	{
		int low = 0;
		int high = _segments.Count - 1;

		while (low < high)
		{
			int mid =
				low + (high - low + 1) / 2;
			if (_segments[mid].StartTick
				<= trackerTick)
			{
				low = mid;
			}
			else
			{
				high = mid - 1;
			}
		}

		return _segments[low];
	}

	private static double TempoAtTickInSegment(
		Segment segment,
		double trackerTick)
	{
		double fraction =
			(trackerTick - segment.StartTick)
				/ segment.TickSpan;
		return segment.StartTempo
			+ segment.TempoDelta * fraction;
	}

	private static void ValidateTempo(
		double tempo,
		string parameterName)
	{
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(
				parameterName);
		}
	}

	private static void ValidateTickSpan(
		double trackerTicks)
	{
		if (!(trackerTicks > 0.0)
			|| double.IsNaN(trackerTicks)
			|| double.IsInfinity(trackerTicks))
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerTicks));
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
