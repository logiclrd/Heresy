using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Smooth panbrello curve which exactly matches IT at tracker tick anchors and
/// linearly interpolates the spatial offset between them.
/// </summary>
public sealed class TrackerPanbrelloCurve
{
	private readonly byte _initialPhase;
	private readonly byte _speed;
	private readonly byte _depth;
	private readonly double _framesPerTick;
	private readonly int _ticksPerRow;

	public TrackerPanbrelloCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TimeSpan tickDuration,
		int ticksPerRow,
		int sampleRate)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double framesPerTick =
			tickDuration.TotalSeconds * sampleRate;
		if (!(framesPerTick > 0.0)
			|| double.IsNaN(framesPerTick)
			|| double.IsInfinity(framesPerTick))
		{
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		}

		_initialPhase = initialPhase;
		_speed = speed;
		_depth = depth;
		_framesPerTick = framesPerTick;
		_ticksPerRow = ticksPerRow;
	}

	public double FramesPerTick => _framesPerTick;

	public double GetSpatialXOffset(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double tickPosition = Math.Min(
			frameOffset / _framesPerTick,
			_ticksPerRow - 1.0);

		int tick0 = (int)Math.Floor(tickPosition);
		int tick1 = Math.Min(
			tick0 + 1,
			_ticksPerRow - 1);
		double fraction = tickPosition - tick0;

		byte phase0 =
			TrackerPanbrello.AdvancePhase(
				_initialPhase,
				_speed,
				tick0);
		byte phase1 =
			TrackerPanbrello.AdvancePhase(
				_initialPhase,
				_speed,
				tick1);

		double offset0 =
			TrackerPanbrello.GetSpatialXOffset(
				phase0,
				_depth);
		double offset1 =
			TrackerPanbrello.GetSpatialXOffset(
				phase1,
				_depth);

		return offset0
			+ (offset1 - offset0) * fraction;
	}
}
