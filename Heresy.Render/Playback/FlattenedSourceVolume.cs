using System;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Playback;

/// <summary>
/// A flattened Pattern/Sequence acts as one logical instigating note for
/// note-volume automation, despite producing many independent audible voices.
/// Descendant voices retain this object by reference after the instigator's
/// logical channel has moved on to another note. A separate instance exists
/// for every invocation scope, so overlapping/nested starts never share
/// volume state merely because they use the same physical host.
/// </summary>
internal sealed class FlattenedSourceVolume
{
	private readonly TrackerTickClock _clock;
	private readonly int _sampleRate;
	private readonly PlaybackOperatorCollection _operators = new();
	private LinearRowPlaybackOperator? _slide;
	private long _slideStartFrame;
	private int _slideTicksPerRow;
	private double _volume;

	public FlattenedSourceVolume(long scopeId, double volume,
		TrackerTickClock clock, int sampleRate)
	{
		if (scopeId <= 0)
			throw new ArgumentOutOfRangeException(nameof(scopeId));
		if (volume < 0 || volume > 1 || !double.IsFinite(volume))
			throw new ArgumentOutOfRangeException(nameof(volume));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		ScopeId = scopeId;
		_volume = volume;
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_sampleRate = sampleRate;
	}

	public long ScopeId { get; }

	public double Read(long frame)
	{
		if (_slide is not null)
			_slide.Update(frame / (double)_sampleRate, RowTime(frame));
		return Math.Clamp(_volume +
			_operators.GetPersistentTotalDelta(PlaybackParameter.NoteVolume),
			0, 1);
	}

	public void Set(long frame, double volume)
	{
		if (volume < 0 || volume > 1 || !double.IsFinite(volume))
			throw new ArgumentOutOfRangeException(nameof(volume));
		Cancel();
		_volume = volume;
	}

	public double Adjust(long frame, double trackerUnits)
	{
		if (!double.IsFinite(trackerUnits))
			throw new ArgumentOutOfRangeException(nameof(trackerUnits));
		double volume = Math.Clamp(Read(frame) + trackerUnits / 64.0, 0, 1);
		Set(frame, volume);
		return volume;
	}

	public void Slide(long frame, int ticksPerRow, double trackerUnitsPerTick)
	{
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (!double.IsFinite(trackerUnitsPerTick))
			throw new ArgumentOutOfRangeException(nameof(trackerUnitsPerTick));
		Commit(frame);
		_slideStartFrame = frame;
		_slideTicksPerRow = ticksPerRow;
		_slide = new LinearRowPlaybackOperator(
			PlaybackParameter.NoteVolume,
			trackerUnitsPerTick * Math.Max(0, ticksPerRow - 1) / 64.0,
			ticksPerRow,
			commitOnExpire: true);
		_operators.Add(_slide);
	}

	public double Clear(long frame)
	{
		Commit(frame);
		return _volume;
	}

	private double RowTime(long frame)
		=> Math.Clamp(_clock.GetElapsedTicks(
			_slideStartFrame, Math.Max(frame, _slideStartFrame)),
			0, _slideTicksPerRow);

	private void Commit(long frame)
	{
		if (_slide is null)
			return;
		var delta = _operators.Expire(_slide,
			frame / (double)_sampleRate, RowTime(frame));
		_volume = Math.Clamp(_volume +
			delta[PlaybackParameter.NoteVolume], 0, 1);
		_slide = null;
	}

	private void Cancel()
	{
		if (_slide is null)
			return;
		_operators.Remove(_slide);
		_slide = null;
	}
}
