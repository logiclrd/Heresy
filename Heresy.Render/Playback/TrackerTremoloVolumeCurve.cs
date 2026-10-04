using System;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Playback;

/// <summary>
/// Smooth tremolo curve whose phase rate and legacy anchor values match IT,
/// while output samples between ticks receive continuous values.
/// </summary>
public sealed class TrackerTremoloVolumeCurve
{
	private readonly double _initialPhase;
	private readonly double _phasePerOutputFrame;
	private readonly double _framesPerTick;
	private readonly byte _depth;
	private readonly TrackerWaveform _waveform;
	private readonly ulong _randomSeed;
	private readonly long _randomStartIndex;

	public TrackerTremoloVolumeCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TimeSpan tickDuration,
		int sampleRate,
		TrackerWaveform waveform = TrackerWaveform.Sine,
		ulong randomSeed = 0,
		long randomStartIndex = 0)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (randomStartIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(randomStartIndex));

		_initialPhase = initialPhase;
		_depth = depth;
		_waveform = waveform;
		_randomSeed = randomSeed;
		_randomStartIndex = randomStartIndex;

		double tickFrames = tickDuration.TotalSeconds * sampleRate;
		if (!(tickFrames > 0.0)
			|| double.IsNaN(tickFrames)
			|| double.IsInfinity(tickFrames))
		{
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		}

		_framesPerTick = tickFrames;
		_phasePerOutputFrame = speed * 4.0 / tickFrames;
	}

	public double GetOffsetTrackerUnits(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		if (_waveform == TrackerWaveform.Random)
		{
			double tickPosition = frameOffset / _framesPerTick;
			long tick0 = (long)Math.Floor(tickPosition);
			long tick1 = checked(tick0 + 1);
			double fraction = tickPosition - tick0;

			double units0 =
				TrackerTremolo.GetRandomVolumeOffsetUnits(
					_randomSeed,
					checked(_randomStartIndex + tick0),
					_depth);
			double units1 =
				TrackerTremolo.GetRandomVolumeOffsetUnits(
					_randomSeed,
					checked(_randomStartIndex + tick1),
					_depth);

			return units0 + (units1 - units0) * fraction;
		}

		double phase =
			_initialPhase + frameOffset * _phasePerOutputFrame;

		return TrackerTremolo.GetContinuousVolumeOffsetUnits(
			_waveform,
			phase,
			_depth);
	}
}
