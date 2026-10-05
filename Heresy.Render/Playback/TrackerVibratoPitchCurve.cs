using System;

using Heresy.Core.Sequencing;
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
	private readonly double _framesPerTick;
	private readonly byte _speed;
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;
	private readonly byte _depth;
	private readonly TrackerWaveform _waveform;
	private readonly ulong _randomSeed;
	private readonly long _randomStartIndex;
	private readonly double _depthScale;

	public TrackerVibratoPitchCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TimeSpan tickDuration,
		int sampleRate,
		TrackerWaveform waveform = TrackerWaveform.Sine,
		ulong randomSeed = 0,
		long randomStartIndex = 0,
		double depthScale = 1.0)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (randomStartIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(randomStartIndex));

		if (!(depthScale >= 0.0)
			|| double.IsNaN(depthScale)
			|| double.IsInfinity(depthScale))
		{
			throw new ArgumentOutOfRangeException(nameof(depthScale));
		}

		_initialPhase = initialPhase;
		_speed = speed;
		_depth = depth;
		_waveform = waveform;
		_randomSeed = randomSeed;
		_randomStartIndex = randomStartIndex;
		_depthScale = depthScale;

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

	public TrackerVibratoPitchCurve(
		byte initialPhase,
		byte speed,
		byte depth,
		TrackerTickClock tickClock,
		long startFrame,
		TrackerWaveform waveform = TrackerWaveform.Sine,
		ulong randomSeed = 0,
		long randomStartIndex = 0,
		double depthScale = 1.0)
	{
		ArgumentNullException.ThrowIfNull(tickClock);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (!Enum.IsDefined(waveform))
			throw new ArgumentOutOfRangeException(nameof(waveform));
		if (randomStartIndex < 0)
			throw new ArgumentOutOfRangeException(nameof(randomStartIndex));
		if (!(depthScale >= 0.0)
			|| double.IsNaN(depthScale)
			|| double.IsInfinity(depthScale))
			throw new ArgumentOutOfRangeException(nameof(depthScale));

		_initialPhase = initialPhase;
		_speed = speed;
		_depth = depth;
		_waveform = waveform;
		_randomSeed = randomSeed;
		_randomStartIndex = randomStartIndex;
		_depthScale = depthScale;
		_tickClock = tickClock;
		_startFrame = startFrame;
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		if (_waveform == TrackerWaveform.Random)
		{
			double tickPosition = _tickClock is null
				? frameOffset / _framesPerTick
				: _tickClock.GetElapsedTicks(
					_startFrame,
					checked(_startFrame + frameOffset));
			long tick0 = (long)Math.Floor(tickPosition);
			long tick1 = checked(tick0 + 1);
			double fraction = tickPosition - tick0;

			double units0 =
				TrackerVibrato.GetRandomLinearSlideUnits(
					_randomSeed,
					checked(_randomStartIndex + tick0),
					_depth);
			double units1 =
				TrackerVibrato.GetRandomLinearSlideUnits(
					_randomSeed,
					checked(_randomStartIndex + tick1),
					_depth);
			double randomUnits =
				(units0 + (units1 - units0) * fraction)
					* _depthScale;

			return Math.Pow(
				2.0,
				randomUnits / TrackerVibrato.LinearSlideUnitsPerOctave);
		}

		double elapsedTicks = _tickClock is null
			? frameOffset / _framesPerTick
			: _tickClock.GetElapsedTicks(
				_startFrame,
				checked(_startFrame + frameOffset));

		double phase =
			_initialPhase + elapsedTicks * _speed * 4.0;
		double continuousUnits =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				_waveform,
				phase,
				_depth)
				* _depthScale;

		return Math.Pow(
			2.0,
				continuousUnits / TrackerVibrato.LinearSlideUnitsPerOctave);
	}
}
