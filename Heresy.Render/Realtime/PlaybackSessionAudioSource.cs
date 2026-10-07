using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Sequencing;
using Heresy.Render.Playback;

namespace Heresy.Render.Realtime;

public sealed class PlaybackSessionAudioSource
	: ILiveAudioOutputSource
{
	private readonly PlaybackSession _playbackSession;
	private readonly ConcurrentQueue<LivePlaybackEvent> _liveEvents = new();

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

	public void EnqueueLiveEvent(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ArgumentNullException.ThrowIfNull(commands);

		_liveEvents.Enqueue(
			new LivePlaybackEvent(
				target,
				commands.ToArray()));
	}

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

		while (_liveEvents.TryDequeue(
			out LivePlaybackEvent? liveEvent))
		{
			_playbackSession.ApplyLiveEvent(
				liveEvent.Target,
				liveEvent.Commands);
		}

		_playbackSession.Render(
			_playbackSession.NextFrame,
			frameCount,
			destination);
	}
}
