using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Traditional tracker arpeggio. This effect is intentionally discrete:
/// tick 0 uses the base pitch, tick 1 the first semitone offset, tick 2 the
/// second, then the three-tick pattern repeats.
/// </summary>
public sealed class TrackerArpeggioPitchCurve : PitchCurve
{
	private readonly byte _firstSemitones;
	private readonly byte _secondSemitones;
	private readonly double _framesPerTick;
	private readonly TrackerTickClock? _tickClock;
	private readonly long _startFrame;

	public TrackerArpeggioPitchCurve(
		byte firstSemitones,
		byte secondSemitones,
		TimeSpan tickDuration,
		int sampleRate)
	{
		if (tickDuration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(tickDuration));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_firstSemitones = firstSemitones;
		_secondSemitones = secondSemitones;
		_framesPerTick = tickDuration.TotalSeconds * sampleRate;
	}

	public TrackerArpeggioPitchCurve(
		byte firstSemitones,
		byte secondSemitones,
		TrackerTickClock tickClock,
		long startFrame)
	{
		ArgumentNullException.ThrowIfNull(tickClock);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));

		_firstSemitones = firstSemitones;
		_secondSemitones = secondSemitones;
		_tickClock = tickClock;
		_startFrame = startFrame;
	}

	public override double GetMultiplier(long frameOffset)
	{
		if (frameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(frameOffset));

		double elapsedTicks = _tickClock is null
			? frameOffset / _framesPerTick
			: _tickClock.GetElapsedTicks(
				_startFrame,
				checked(_startFrame + frameOffset));

		long tick = (long)Math.Floor(elapsedTicks);

		return (tick % 3) switch
		{
			0 => 1.0,
			1 => Math.Pow(2.0, _firstSemitones / 12.0),
			_ => Math.Pow(2.0, _secondSemitones / 12.0),
		};
	}
}
