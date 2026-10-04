using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Smooth tremolo curve whose phase rate and legacy anchor values match IT,
/// while output samples between ticks receive continuous values.
/// </summary>
public sealed class TrackerTremoloVolumeCurve
{
	private readonly double _initialPhase;
	private readonly double _phasePerOutputFrame;
	private readonly byte _depth;

	public TrackerTremoloVolumeCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TimeSpan tickDuration,
		int sampleRate)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_initialPhase = initialPhase;
		_depth = depth;

		double tickFrames = tickDuration.TotalSeconds * sampleRate;
		if (!(tickFrames > 0.0)
			|| double.IsNaN(tickFrames)
			|| double.IsInfinity(tickFrames))
		{
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		}

		_phasePerOutputFrame = speed * 4.0 / tickFrames;
	}

	public double GetOffsetTrackerUnits(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double phase =
			_initialPhase + frameOffset * _phasePerOutputFrame;

		return TrackerTremolo.GetContinuousVolumeOffsetUnits(
			phase,
			_depth);
	}
}
