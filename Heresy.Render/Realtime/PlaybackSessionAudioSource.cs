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
	private sealed record QueuedPlaybackAction(
		LivePlaybackEvent? Event, long? InvocationId,
		long? CancelInvocationId = null);

	private readonly ConcurrentQueue<QueuedPlaybackAction> _liveEvents = new();

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

		_liveEvents.Enqueue(new QueuedPlaybackAction(
			new LivePlaybackEvent(target, commands.ToArray()), null));
	}

	/// <summary>
	/// Enqueue a rendered Pattern event with a stable invocation-local
	/// virtual voice namespace. Ordinary live preview events remain
	/// separately keyed by virtual channel ID.
	/// </summary>
	public void EnqueueScopedEvent(
		long invocationId, ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ArgumentNullException.ThrowIfNull(commands);
		if (invocationId < 0)
			throw new ArgumentOutOfRangeException(nameof(invocationId));
		_liveEvents.Enqueue(new QueuedPlaybackAction(
			new LivePlaybackEvent(target, commands.ToArray()), invocationId));
	}

	/// <summary>Drop all currently sounding virtual voices from a
	/// cancelled Pattern invocation. Completion alone does not cut them.</summary>
	public void EnqueueCancelScope(long invocationId)
	{
		if (invocationId < 0)
			throw new ArgumentOutOfRangeException(nameof(invocationId));
		_liveEvents.Enqueue(new QueuedPlaybackAction(
			null, null, invocationId));
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
			out QueuedPlaybackAction? action))
		{
			if (action.CancelInvocationId is long cancelled)
				_playbackSession.CancelScopedVoices(cancelled);
			else if (action.InvocationId is long owner)
				_playbackSession.ApplyScopedEvent(owner,
					action.Event!.Target, action.Event.Commands);
			else
				_playbackSession.ApplyLiveEvent(
					action.Event!.Target, action.Event.Commands);
		}

		_playbackSession.Render(
			_playbackSession.NextFrame,
			frameCount,
			destination);
	}
}
