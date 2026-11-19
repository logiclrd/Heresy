using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

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
		Sha256 = ComputeSha256(_samples);
	}

	public int SampleRate { get; }
	public int ChannelCount { get; }
	public long FrameCount { get; }

	/// <summary>
	/// Content SHA-256 of the immutable interleaved IEEE-754 PCM bits.
	/// Calculated exactly once when this PCM buffer is constructed; no caller
	/// can supply or overwrite the digest. Independent of sample metadata,
	/// encoded asset bytes, loop settings and channel positioning.
	/// </summary>
	public string Sha256 { get; }

	private static string ComputeSha256(ReadOnlySpan<float> samples)
	{
		using IncrementalHash hash =
			IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Span<byte> buffer = stackalloc byte[4096];
		int used = 0;
		foreach (float sample in samples)
		{
			BinaryPrimitives.WriteInt32LittleEndian(
				buffer.Slice(used, sizeof(int)),
				BitConverter.SingleToInt32Bits(sample));
			used += sizeof(int);
			if (used == buffer.Length)
			{
				hash.AppendData(buffer);
				used = 0;
			}
		}
		if (used != 0)
			hash.AppendData(buffer[..used]);
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	/// <summary>Compare immutable PCM, checking the cached SHA-256 first and
	/// inspecting every sample bit only when the digests agree. The digest is
	/// an optimization, not the ultimate proof of equality.</summary>
	public bool HasSameAudio(SamplePcmData? other)
	{
		if (ReferenceEquals(this, other))
			return true;
		if (other is null || SampleRate != other.SampleRate
			|| ChannelCount != other.ChannelCount
			|| FrameCount != other.FrameCount
			|| !string.Equals(Sha256, other.Sha256, StringComparison.Ordinal))
			return false;
		for (int index = 0; index < _samples.Length; index++)
			if (BitConverter.SingleToInt32Bits(_samples[index])
				!= BitConverter.SingleToInt32Bits(other._samples[index]))
				return false;
		return true;
	}

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
