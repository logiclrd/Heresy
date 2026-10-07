using System;

namespace Heresy.Core.Samples;

/// <summary>
/// Immutable interleaved floating-point PCM owned by a live sample definition.
/// File/archive formats are decoded before this data reaches realtime playback.
/// </summary>
public sealed class SamplePcmData
{
	private readonly float[] _samples;

	public SamplePcmData(
		int sampleRate,
		int channelCount,
		ReadOnlySpan<float> interleavedSamples)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		if (interleavedSamples.Length % channelCount != 0)
		{
			throw new ArgumentException(
				"Interleaved sample count must be divisible by channel count.",
				nameof(interleavedSamples));
		}

		SampleRate = sampleRate;
		ChannelCount = channelCount;
		_samples = interleavedSamples.ToArray();
		FrameCount = _samples.LongLength / channelCount;
	}

	public int SampleRate { get; }
	public int ChannelCount { get; }
	public long FrameCount { get; }

	public float GetSample(long frame, int channel)
	{
		if ((ulong)frame >= (ulong)FrameCount)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if ((uint)channel >= (uint)ChannelCount)
			throw new ArgumentOutOfRangeException(nameof(channel));

		long index = checked(frame * ChannelCount + channel);
		return _samples[checked((int)index)];
	}
}
