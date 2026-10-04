using System;
using System.Collections.Generic;

namespace Heresy.Render.Sounds;

/// <summary>
/// Piecewise pitch curves in invocation-frame coordinates. Position is the
/// accumulated per-output-sample relative playback distance.
/// </summary>
public sealed class PitchTrajectory
{
	private sealed class Segment
	{
		public required long StartFrame { get; init; }
		public required double StartPosition { get; init; }
		public required PitchCurve Curve { get; init; }

		public long? EndFrameExclusive { get; set; }
		public double? EndPosition { get; set; }

		private long _cachedFrameCount;
		private double _cachedPosition;

		public double GetPositionDelta(long frameCount)
		{
			if (frameCount < 0)
				throw new ArgumentOutOfRangeException(nameof(frameCount));

			if (frameCount == _cachedFrameCount)
				return _cachedPosition;

			if (frameCount > _cachedFrameCount)
			{
				double position = _cachedPosition;
				for (long frame = _cachedFrameCount; frame < frameCount; frame++)
					position += Curve.GetMultiplier(frame);

				_cachedFrameCount = frameCount;
				_cachedPosition = position;
				return position;
			}

			return Curve.GetIntegratedPosition(frameCount);
		}
	}

	private readonly List<Segment> _segments =
	[
		new Segment
		{
			StartFrame = 0,
			StartPosition = 0.0,
			Curve = new ConstantPitchCurve(1.0),
		},
	];

	public double CurrentMultiplier => _segments[^1].Curve.GetMultiplier(0);

	public bool CanProjectEndEfficiently =>
		_segments[^1].Curve.CanProjectEndEfficiently;

	public void SetMultiplier(long frame, double multiplier)
		=> SetCurve(frame, new ConstantPitchCurve(multiplier));

	public void SetCurve(long frame, PitchCurve curve)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		ArgumentNullException.ThrowIfNull(curve);

		Segment last = _segments[^1];
		if (frame < last.StartFrame)
		{
			throw new InvalidOperationException(
				"Pitch-trajectory segments must be added chronologically.");
		}

		if (frame == last.StartFrame)
		{
			_segments[^1] = new Segment
			{
				StartFrame = frame,
				StartPosition = last.StartPosition,
				Curve = curve,
			};
			return;
		}

		long frameCount = frame - last.StartFrame;
		double endPosition =
			last.StartPosition + last.GetPositionDelta(frameCount);

		last.EndFrameExclusive = frame;
		last.EndPosition = endPosition;

		_segments.Add(new Segment
		{
			StartFrame = frame,
			StartPosition = endPosition,
			Curve = curve,
		});
	}

	public double GetMultiplier(long frame)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));

		Segment segment = FindSegment(frame);
		return segment.Curve.GetMultiplier(frame - segment.StartFrame);
	}

	public double GetPosition(long frame)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));

		Segment segment = FindSegment(frame);
		return segment.StartPosition
			+ segment.GetPositionDelta(frame - segment.StartFrame);
	}

	/// <summary>
	/// Finds the first output frame whose accumulated relative playback
	/// position reaches or exceeds position. The current open curve must support
	/// efficient indefinite projection.
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

		foreach (Segment segment in _segments)
		{
			if (segment.EndFrameExclusive.HasValue)
			{
				double endPosition = segment.EndPosition!.Value;
				if (position > endPosition)
					continue;

				return FindWithinClosedSegment(segment, position);
			}

			if (segment.Curve is not ConstantPitchCurve constant)
			{
				throw new InvalidOperationException(
					"The current pitch curve cannot be projected efficiently to an arbitrary end frame.");
			}

			double remaining = position - segment.StartPosition;
			if (remaining <= 0.0)
				return segment.StartFrame;

			double frames = Math.Ceiling(remaining / constant.Multiplier);
			if (frames >= long.MaxValue - segment.StartFrame)
				return long.MaxValue;

			return segment.StartFrame + (long)frames;
		}

		throw new InvalidOperationException("Pitch trajectory contains no open segment.");
	}

	private long FindWithinClosedSegment(Segment segment, double position)
	{
		long low = segment.StartFrame;
		long high = segment.EndFrameExclusive!.Value;

		while (low < high)
		{
			long middle = low + (high - low) / 2;
			double middlePosition =
				segment.StartPosition
				+ segment.GetPositionDelta(middle - segment.StartFrame);

			if (middlePosition >= position)
				high = middle;
			else
				low = middle + 1;
		}

		return low;
	}

	private Segment FindSegment(long frame)
	{
		int low = 0;
		int high = _segments.Count - 1;

		while (low < high)
		{
			int middle = low + (high - low + 1) / 2;
			if (_segments[middle].StartFrame <= frame)
				low = middle;
			else
				high = middle - 1;
		}

		return _segments[low];
	}
}
