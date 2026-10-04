using System;
using System.Numerics;

namespace Heresy.Render.Sounds;

/// <summary>
/// Mutable state for one invocation of a stateless sound renderer.
/// </summary>
public abstract class SoundState
{
	private readonly PitchTrajectory _pitchTrajectory = new();
	private double _pitchMultiplier = 1.0;
	private double _playbackSpeedMultiplier = 1.0;
	private TimeSpan _playbackOffset;
	private Vector3 _position;
	private TimeSpan? _noteOffTime;
	private long _playbackOriginFrame;
	private long? _naturalEndFrameExclusive;

	public double PitchMultiplier
	{
		get => _pitchMultiplier;
		set => _pitchMultiplier = ValidatePositiveFinite(value, nameof(value));
	}

	public PitchTrajectory PitchTrajectory => _pitchTrajectory;

	public double PlaybackSpeedMultiplier
	{
		get => _playbackSpeedMultiplier;
		set => _playbackSpeedMultiplier = ValidatePositiveFinite(value, nameof(value));
	}

	public TimeSpan PlaybackOffset
	{
		get => _playbackOffset;
		set
		{
			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value));
			_playbackOffset = value;
		}
	}

	public Vector3 Position
	{
		get => _position;
		set
		{
			if (!float.IsFinite(value.X)
				|| !float.IsFinite(value.Y)
				|| !float.IsFinite(value.Z))
			{
				throw new ArgumentOutOfRangeException(nameof(value));
			}
			_position = value;
		}
	}

	public long PlaybackOriginFrame => _playbackOriginFrame;

	public long? NaturalEndFrameExclusive => _naturalEndFrameExclusive;

	internal void RestartPlaybackAt(long frame)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));

		_playbackOriginFrame = frame;
		_naturalEndFrameExclusive = null;
	}

	internal void MarkNaturalEndReached(long frameExclusive)
	{
		if (frameExclusive < 0)
			throw new ArgumentOutOfRangeException(nameof(frameExclusive));

		if (!_naturalEndFrameExclusive.HasValue
			|| frameExclusive < _naturalEndFrameExclusive.Value)
		{
			_naturalEndFrameExclusive = frameExclusive;
		}
	}

	public TimeSpan? NoteOffTime
	{
		get => _noteOffTime;
		set
		{
			if (value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(value));
			_noteOffTime = value;
		}
	}

	private static double ValidatePositiveFinite(double value, string paramName)
	{
		if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(paramName);
		return value;
	}
}
