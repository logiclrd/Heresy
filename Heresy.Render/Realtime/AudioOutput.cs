using System;

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
