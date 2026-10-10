using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Render.Playback;

namespace Heresy.Render.Realtime;

public readonly record struct AudioOutputFormat
{
	public AudioOutputFormat(
		int sampleRate,
		int channelCount)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));

		SampleRate = sampleRate;
		ChannelCount = channelCount;
	}

	public int SampleRate { get; }
	public int ChannelCount { get; }
	public int SamplesPerFrame => ChannelCount;
}

public interface IAudioOutputSource
{
	AudioOutputFormat Format { get; }

	void Render(
		int frameCount,
		Span<float> destination);
}

/// <summary>
/// A finite arrangement is generated incrementally rather than compiled
/// up front. Offline renderers request the remaining logical frames, then
/// drain the exposed session's existing release tails.
/// </summary>
public interface IIncrementalArrangementSource : IAudioOutputSource
{
	PlaybackSession Session { get; }
	bool IsComplete { get; }
	TimeSpan LogicalDuration { get; }
	int RenderLogical(int frameCount, Span<float> destination);
}

public sealed record LivePlaybackEvent(
	ChannelTarget Target,
	IReadOnlyList<NoteCommand> Commands);

public interface ILiveAudioOutputSource : IAudioOutputSource
{
	void EnqueueLiveEvent(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands);
}

/// <summary>Optional lock-free counter exposed by a realtime output session.</summary>
public interface IAudioOutputUnderrunCounter
{
	long UnderrunCount { get; }
}

/// <summary>
/// Stable snapshot from an active or stopped output session. SessionId advances
/// on every replacement/stop, so stale callback reports cannot revive old UI.
/// </summary>
public readonly record struct PlaybackAudioHealthSnapshot(
	long SessionId,
	bool IsActive,
	long UnderrunCount,
	Exception? Fault);

public interface IAudioOutputSession : IDisposable
{
	AudioOutputFormat Format { get; }
	bool IsRunning { get; }
	Exception? Fault { get; }

	void Start();
	void Stop();
}

public interface IAudioOutputBackend : IDisposable
{
	IAudioOutputSession Open(
		AudioOutputFormat format,
		IAudioOutputSource source);
}
