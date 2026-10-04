using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Runtime state for one physical playback/tracker channel.
/// </summary>
public sealed class PlaybackChannelState
{
	private readonly float[] _previousSourceFrame;
	private readonly float[] _lastSourceFrame;
	private int _sourceHistoryFrames;

	internal PlaybackChannelState(int outputChannelCount, int sampleRate)
	{
		_previousSourceFrame = new float[outputChannelCount];
		_lastSourceFrame = new float[outputChannelCount];
		AntiClickTail = new AntiClickTail(outputChannelCount, sampleRate);
	}

	public ISound? CurrentSound { get; internal set; }
	public SoundState? CurrentSoundState { get; internal set; }
	public long NoteStartFrame { get; internal set; }

	public double NoteVolume { get; internal set; } = 1.0;
	public double OverallVolume { get; internal set; } = 1.0;

	public AntiClickTail AntiClickTail { get; }

	internal bool HasCurrentSound => CurrentSound is not null && CurrentSoundState is not null;

	internal void ObserveSourceFrame(ReadOnlySpan<float> sourceFrame)
	{
		if (sourceFrame.Length != _lastSourceFrame.Length)
			throw new ArgumentException("Source frame width does not match output channel count.", nameof(sourceFrame));

		if (_sourceHistoryFrames != 0)
			_lastSourceFrame.CopyTo(_previousSourceFrame);

		sourceFrame.CopyTo(_lastSourceFrame);
		if (_sourceHistoryFrames < 2)
			_sourceHistoryFrames++;
	}

	internal void CutCurrentSound()
	{
		if (HasCurrentSound && _sourceHistoryFrames != 0)
		{
			AntiClickTail.AddCut(
				_previousSourceFrame,
				_lastSourceFrame,
				havePrevious: _sourceHistoryFrames >= 2);
		}

		DetachCurrentSound();
	}

	internal void DetachCurrentSound()
	{
		CurrentSound = null;
		CurrentSoundState = null;
		_sourceHistoryFrames = 0;
		Array.Clear(_previousSourceFrame);
		Array.Clear(_lastSourceFrame);
	}
}
