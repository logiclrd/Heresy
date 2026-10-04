using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Smooth tracker vibrato curve. At traditional tick phases it produces the
/// same IT-compatible pitch values, while output samples between ticks receive
/// interpolated phase values through the original fine-sine waveform.
/// </summary>
public sealed class TrackerVibratoPitchCurve : PitchCurve
{
	private readonly double _initialPhase;
	private readonly double _phasePerOutputFrame;
	private readonly byte _depth;

	public TrackerVibratoPitchCurve(
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

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double phase = _initialPhase + frameOffset * _phasePerOutputFrame;
		return TrackerVibrato.GetContinuousPitchMultiplier(phase, _depth);
	}
}
