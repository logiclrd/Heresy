using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Samples;

public sealed class SampleSoundState : SoundState
{
	private long _sourceFrameOffset;

	// A live Synth audition alone may retain a small history of *already
	// integrated* Sample positions. These are the actual streaming doubles,
	// not a reconstruction from current pitch or a replay of prior audio.
	// A 128-frame cap also bounds the cost of copying recent positions into
	// a worker-prepared Multi onset recipient at an audio boundary.
	internal const int LiveOnsetPositionHistoryFrames = 128;
	private double[]? _recentPitchPositions;
	private long _lastRecordedPitchFrame = -1;
	private int _recordedPitchFrames;

	internal void EnableLiveOnsetPositionHistory()
		=> _recentPitchPositions ??= new double[LiveOnsetPositionHistoryFrames];

	internal void RecordLiveOnsetPitchPosition(long frame, double position)
	{
		if (_recentPitchPositions is null)
			return;
		if (frame < 0 || !double.IsFinite(position) || position < 0.0)
			throw new InvalidOperationException(
				"Live Sample pitch positions must be finite and nonnegative.");
		if (_lastRecordedPitchFrame != frame - 1)
			_recordedPitchFrames = 0;
		_recentPitchPositions[(int)(frame % LiveOnsetPositionHistoryFrames)] = position;
		_lastRecordedPitchFrame = frame;
		_recordedPitchFrames = Math.Min(
			LiveOnsetPositionHistoryFrames, _recordedPitchFrames + 1);
	}

	internal bool TryGetRecordedPitchPosition(long frame, out double position)
	{
		if (_recentPitchPositions is not null
			&& frame >= 0 && frame <= _lastRecordedPitchFrame
			&& _lastRecordedPitchFrame - frame < _recordedPitchFrames)
		{
			position = _recentPitchPositions[(int)(frame % LiveOnsetPositionHistoryFrames)];
			return double.IsFinite(position) && position >= 0.0;
		}
		position = 0.0;
		return false;
	}

	/// <summary>A variable-rate rephase can use the exact recorded integral
	/// at a rewound frame, or the current live trajectory if skip is unchanged.
	/// No future variable-rate frames may be projected speculatively.</summary>
	internal bool CanTransferLiveOnsetPosition(long oldFrame, long newFrame)
		=> _recentPitchPositions is not null
			&& oldFrame > 0 && newFrame >= 0 && newFrame <= oldFrame
			&& _lastRecordedPitchFrame == oldFrame - 1
			&& (newFrame == oldFrame
				|| TryGetRecordedPitchPosition(newFrame, out _));

	/// <summary>Retain bounded, exact prior integrals when a detached
	/// Sample takes over a shorter local onset clock. The outgoing and
	/// incoming histories remain independent after the atomic copy.</summary>
	internal void CopyLiveOnsetPositionsBeforeFrame(
		SampleSoundState source, long newFrame)
	{
		if (_recentPitchPositions is null || source._recentPitchPositions is null)
			return;
		long first = Math.Max(0, Math.Max(
			newFrame - LiveOnsetPositionHistoryFrames,
			source._lastRecordedPitchFrame - source._recordedPitchFrames + 1));
		int copied = 0;
		for (long frame = first; frame < newFrame; frame++)
		{
			if (!source.TryGetRecordedPitchPosition(frame, out double position))
				break;
			_recentPitchPositions[(int)(frame % LiveOnsetPositionHistoryFrames)] = position;
			copied++;
		}
		_lastRecordedPitchFrame = newFrame - 1;
		_recordedPitchFrames = copied;
	}


	/// <summary>
	/// Invocation-local policy selected by InstrumentSound after its final
	/// volume envelope has been resolved. A bare sample still ends at Note
	/// Off; an Instrument-selected sample stays active while that envelope
	/// performs its release. This never changes a shared SampleDefinition.
	/// </summary>
	internal bool ReleaseIsControlledByVolumeEnvelope { get; set; }

	public long SourceFrameOffset
	{
		get => _sourceFrameOffset;
		internal set
		{
			if (value < 0)
				throw new ArgumentOutOfRangeException(nameof(value));
			_sourceFrameOffset = value;
		}
	}
}
