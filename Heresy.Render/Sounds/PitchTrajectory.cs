using System;
using System.Collections.Generic;

namespace Heresy.Render.Sounds;

/// <summary>
/// Piecewise-constant relative pitch multiplier as a function of invocation
/// output frame. The integrated position is measured in unmodulated output
/// frames and can therefore be multiplied by a sound's ordinary base step.
/// </summary>
public sealed class PitchTrajectory
{
	private sealed class ControlPoint
	{
		public required long Frame { get; init; }
		public required double Position { get; init; }
		public required double Multiplier { get; set; }
	}

	private readonly List<ControlPoint> _points =
	[
		new ControlPoint
		{
			Frame = 0,
			Position = 0.0,
			Multiplier = 1.0,
		},
	];

	public double CurrentMultiplier => _points[^1].Multiplier;

	public bool IsUnity =>
		_points.Count == 1
		&& _points[0].Multiplier == 1.0;

	/// <summary>
	/// Changes the relative pitch multiplier starting at invocation frame.
	/// Control points must be added chronologically.
	/// </summary>
	public void SetMultiplier(long frame, double multiplier)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if (!(multiplier > 0.0)
			|| double.IsNaN(multiplier)
			|| double.IsInfinity(multiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(multiplier));
		}

		ControlPoint last = _points[^1];
		if (frame < last.Frame)
		{
			throw new InvalidOperationException(
				"Pitch-trajectory control points must be added chronologically.");
		}

		if (frame == last.Frame)
		{
			last.Multiplier = multiplier;
			return;
		}

		double position =
			last.Position
			+ (frame - last.Frame) * last.Multiplier;

		_points.Add(new ControlPoint
		{
			Frame = frame,
			Position = position,
			Multiplier = multiplier,
		});
	}

	/// <summary>
	/// Returns integrated relative playback position at an invocation frame.
	/// </summary>
	public double GetPosition(long frame)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));

		int index = FindPointAtOrBefore(frame);
		ControlPoint point = _points[index];

		return point.Position
			+ (frame - point.Frame) * point.Multiplier;
	}

	/// <summary>
	/// Finds the first integer output frame whose integrated relative playback
	/// position reaches or exceeds the requested position.
	/// </summary>
	public long FindFrameAtOrAfterPosition(double position)
	{
		if (double.IsNaN(position)
			|| double.IsInfinity(position)
			|| position < 0.0)
		{
			throw new ArgumentOutOfRangeException(nameof(position));
		}

		if (position == 0.0)
			return 0;

		for (int index = 0; index < _points.Count; index++)
		{
			ControlPoint point = _points[index];

			if (index + 1 < _points.Count)
			{
				ControlPoint next = _points[index + 1];
				if (position > next.Position)
					continue;
			}

			double frames = (position - point.Position) / point.Multiplier;
			if (frames <= 0.0)
				return point.Frame;

			double ceiling = Math.Ceiling(frames);
			if (ceiling >= long.MaxValue - point.Frame)
				return long.MaxValue;

			return point.Frame + (long)ceiling;
		}

		throw new InvalidOperationException("Pitch trajectory contains no control points.");
	}

	private int FindPointAtOrBefore(long frame)
	{
		int low = 0;
		int high = _points.Count - 1;

		while (low < high)
		{
			int middle = low + (high - low + 1) / 2;
			if (_points[middle].Frame <= frame)
				low = middle;
			else
				high = middle - 1;
		}

		return low;
	}
}
