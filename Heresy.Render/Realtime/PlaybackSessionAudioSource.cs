using System;

using Heresy.Render.Playback;

namespace Heresy.Render.Realtime;

public sealed class PlaybackSessionAudioSource
	: IAudioOutputSource
{
	public PlaybackSessionAudioSource(
		PlaybackSession playbackSession)
	{
		throw new NotImplementedException();
	}

	public AudioOutputFormat Format =>
		throw new NotImplementedException();

	public void Render(
		int frameCount,
		Span<float> destination)
		=> throw new NotImplementedException();
}
