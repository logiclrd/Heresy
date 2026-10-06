using System;

using Heresy.Render.Playback;

namespace Heresy.Render.Realtime;

public sealed class PlaybackSessionAudioSource
	: IAudioOutputSource
{
	private readonly PlaybackSession _playbackSession;

	public PlaybackSessionAudioSource(
		PlaybackSession playbackSession)
	{
		_playbackSession =
			playbackSession
				?? throw new ArgumentNullException(nameof(playbackSession));

		Format =
			new AudioOutputFormat(
				_playbackSession.SampleRate,
				_playbackSession.OutputChannelCount);
	}

	public AudioOutputFormat Format { get; }

	public void Render(
		int frameCount,
		Span<float> destination)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		int requiredSamples =
			checked(
				frameCount
					* Format.ChannelCount);
		if (destination.Length != requiredSamples)
		{
			throw new ArgumentException(
				"Destination length must exactly match frame count and channel count.",
				nameof(destination));
		}

		_playbackSession.Render(
			_playbackSession.NextFrame,
			frameCount,
			destination);
	}
}
