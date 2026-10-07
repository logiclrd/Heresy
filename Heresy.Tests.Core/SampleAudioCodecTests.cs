using System;
using System.Buffers.Binary;
using System.IO;

using AwesomeAssertions;

using Codec.Flac;
using Codec.Mp3;
using Codec.Vorbis;

using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SampleAudioCodecTests
{
	[Test]
	public void DecodesPcm16StereoWave()
	{
		byte[] wave = CreateWave(
			formatTag: 1,
			channels: 2,
			sampleRate: 48000,
			bitsPerSample: 16,
			data:
			[
				0x00, 0x80,
				0xFF, 0x7F,
				0x00, 0x00,
				0x00, 0x40,
			]);

		SamplePcmData data =
			SampleAudioCodec.Decode(
				wave,
				"stereo.wav");

		data.SampleRate.Should().Be(48000);
		data.ChannelCount.Should().Be(2);
		data.FrameCount.Should().Be(2);
		data.GetSample(0, 0)
			.Should().BeApproximately(-1.0f, 1e-6f);
		data.GetSample(0, 1)
			.Should().BeApproximately(
				32767.0f / 32768.0f,
				1e-6f);
		data.GetSample(1, 0)
			.Should().BeApproximately(0.0f, 1e-6f);
		data.GetSample(1, 1)
			.Should().BeApproximately(0.5f, 1e-6f);
	}

	[Test]
	public void DecodesFloat32MonoWave()
	{
		byte[] bytes = new byte[8];
		BinaryPrimitives.WriteInt32LittleEndian(
			bytes.AsSpan(0, 4),
			BitConverter.SingleToInt32Bits(0.25f));
		BinaryPrimitives.WriteInt32LittleEndian(
			bytes.AsSpan(4, 4),
			BitConverter.SingleToInt32Bits(-0.5f));
		byte[] wave = CreateWave(
			formatTag: 3,
			channels: 1,
			sampleRate: 44100,
			bitsPerSample: 32,
			data: bytes);

		SamplePcmData data =
			SampleAudioCodec.Decode(
				wave,
				"float.wav");

		data.FrameCount.Should().Be(2);
		data.GetSample(0, 0)
			.Should().BeApproximately(0.25f, 1e-6f);
		data.GetSample(1, 0)
			.Should().BeApproximately(-0.5f, 1e-6f);
	}

	[Test]
	public void DecodesFlacToImmutablePcm()
	{
		short[] source =
		[
			short.MinValue,
			0,
			16384,
			short.MaxValue,
		];
		byte[] encoded =
			FlacCodec.Encode(
				source,
				sampleRate: 8000,
				channels: 1,
				blockSize: 4,
				compression: FlacSubframeMode.Verbatim);

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.flac");

		data.SampleRate.Should().Be(8000);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().Be(4);
		data.GetSample(0, 0).Should().BeApproximately(-1.0f, 1e-6f);
		data.GetSample(2, 0).Should().BeApproximately(0.5f, 1e-6f);
		data.GetSample(3, 0).Should().BeApproximately(
			32767.0f / 32768.0f,
			1e-6f);
	}

	[Test]
	public void DecodesMp3AtLoadTime()
	{
		const int sampleRate = 8000;
		short[] source =
			CreateSine(
				sampleRate,
				frames: 4096,
				amplitude: 12000);
		byte[] encoded =
			Mp3Encoder.Encode(
				source,
				new Mp3EncoderOptions(
					sampleRate,
					Channels: 1,
					BitrateKbps: 32));

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.mp3");

		data.SampleRate.Should().Be(sampleRate);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().BeGreaterThan(0);
		PeakMagnitude(data).Should().BeGreaterThan(0.05f);
	}

	[Test]
	public void DecodesOggVorbisAtLoadTime()
	{
		const int sampleRate = 8000;
		short[] source =
			CreateSine(
				sampleRate,
				frames: 4096,
				amplitude: 12000);
		byte[] encoded =
			VorbisEncoder.Encode(
				source,
				new VorbisEncoderOptions(
					sampleRate,
					Channels: 1,
					Quality: 0.4f,
					SerialNumber: 7));

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.ogg");

		data.SampleRate.Should().Be(sampleRate);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().BeGreaterThan(0);
		PeakMagnitude(data).Should().BeGreaterThan(0.05f);
	}

	[Test]
	public void DecodesBigEndianPcmAiff()
	{
		byte[] encoded =
			CreateAiff(
				sampleRate: 8000,
				channels: 2,
				samples:
				[
					short.MinValue,
					short.MaxValue,
					0,
					16384,
				]);

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.aiff");

		data.SampleRate.Should().Be(8000);
		data.ChannelCount.Should().Be(2);
		data.FrameCount.Should().Be(2);
		data.GetSample(0, 0).Should().BeApproximately(-1.0f, 1e-6f);
		data.GetSample(0, 1).Should().BeApproximately(
			32767.0f / 32768.0f,
			1e-6f);
		data.GetSample(1, 1).Should().BeApproximately(0.5f, 1e-6f);
	}

	[Test]
	public void UnsupportedAssetIsRejectedClearly()
	{
		Action act = () =>
			SampleAudioCodec.Decode(
				[0x01, 0x02, 0x03, 0x04],
				"sample.xyz");

		act.Should()
			.Throw<NotSupportedException>()
			.WithMessage("*WAVE*FLAC*MP3*OGG*AIFF*");
	}

	private static short[] CreateSine(
		int sampleRate,
		int frames,
		short amplitude)
	{
		short[] result = new short[frames];
		for (int frame = 0; frame < frames; frame++)
		{
			result[frame] =
				(short)(
					Math.Sin(
						2.0 * Math.PI * 440.0 * frame / sampleRate)
					* amplitude);
		}
		return result;
	}

	private static float PeakMagnitude(
		SamplePcmData data)
	{
		float peak = 0;
		for (long frame = 0; frame < data.FrameCount; frame++)
		{
			for (int channel = 0; channel < data.ChannelCount; channel++)
				peak = Math.Max(peak, Math.Abs(data.GetSample(frame, channel)));
		}
		return peak;
	}

	private static byte[] CreateAiff(
		int sampleRate,
		ushort channels,
		short[] samples)
	{
		if (samples.Length % channels != 0)
			throw new ArgumentException("Sample count must divide evenly by channels.");

		using MemoryStream stream = new();
		using BinaryWriter writer =
			new(
				stream,
				System.Text.Encoding.ASCII,
				leaveOpen: true);

		int sampleBytes = checked(samples.Length * 2);
		int commChunkBytes = 18;
		int ssndChunkBytes = checked(8 + sampleBytes);
		int formBytes =
			checked(
				4
					+ 8 + commChunkBytes
					+ 8 + ssndChunkBytes);

		writer.Write(System.Text.Encoding.ASCII.GetBytes("FORM"));
		WriteUInt32BigEndian(writer, checked((uint)formBytes));
		writer.Write(System.Text.Encoding.ASCII.GetBytes("AIFF"));
		writer.Write(System.Text.Encoding.ASCII.GetBytes("COMM"));
		WriteUInt32BigEndian(writer, checked((uint)commChunkBytes));
		WriteUInt16BigEndian(writer, channels);
		WriteUInt32BigEndian(
			writer,
			checked((uint)(samples.Length / channels)));
		WriteUInt16BigEndian(writer, 16);
		writer.Write(EncodeExtended80(sampleRate));
		writer.Write(System.Text.Encoding.ASCII.GetBytes("SSND"));
		WriteUInt32BigEndian(writer, checked((uint)ssndChunkBytes));
		WriteUInt32BigEndian(writer, 0);
		WriteUInt32BigEndian(writer, 0);
		foreach (short sample in samples)
			WriteUInt16BigEndian(writer, unchecked((ushort)sample));

		return stream.ToArray();
	}

	private static byte[] EncodeExtended80(
		int value)
	{
		if (value <= 0)
			throw new ArgumentOutOfRangeException(nameof(value));

		int exponent = 0;
		double normalized = value;
		while (normalized >= 2.0)
		{
			normalized /= 2.0;
			exponent++;
		}
		while (normalized < 1.0)
		{
			normalized *= 2.0;
			exponent--;
		}

		ushort biasedExponent =
			checked((ushort)(exponent + 16383));
		ulong significand =
			checked((ulong)Math.Round(normalized * Math.Pow(2.0, 63)));

		byte[] result = new byte[10];
		BinaryPrimitives.WriteUInt16BigEndian(
			result.AsSpan(0, 2),
			biasedExponent);
		BinaryPrimitives.WriteUInt64BigEndian(
			result.AsSpan(2, 8),
			significand);
		return result;
	}

	private static void WriteUInt16BigEndian(
		BinaryWriter writer,
		ushort value)
	{
		Span<byte> bytes = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
		writer.Write(bytes);
	}

	private static void WriteUInt32BigEndian(
		BinaryWriter writer,
		uint value)
	{
		Span<byte> bytes = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
		writer.Write(bytes);
	}

	private static byte[] CreateWave(
		ushort formatTag,
		ushort channels,
		int sampleRate,
		ushort bitsPerSample,
		byte[] data)
	{
		using MemoryStream stream = new();
		using BinaryWriter writer =
			new(
				stream,
				System.Text.Encoding.ASCII,
				leaveOpen: true);

		ushort blockAlign =
			checked(
				(ushort)(
					channels
						* (bitsPerSample / 8)));
		int byteRate =
			checked(
				sampleRate
					* blockAlign);

		writer.Write(
			System.Text.Encoding.ASCII.GetBytes("RIFF"));
		int padding =
			data.Length & 1;
		writer.Write(
			checked(
				36 + data.Length + padding));
		writer.Write(
			System.Text.Encoding.ASCII.GetBytes("WAVE"));
		writer.Write(
			System.Text.Encoding.ASCII.GetBytes("fmt "));
		writer.Write(16);
		writer.Write(formatTag);
		writer.Write(channels);
		writer.Write(sampleRate);
		writer.Write(byteRate);
		writer.Write(blockAlign);
		writer.Write(bitsPerSample);
		writer.Write(
			System.Text.Encoding.ASCII.GetBytes("data"));
		writer.Write(data.Length);
		writer.Write(data);
		if (padding != 0)
			writer.Write((byte)0);

		return stream.ToArray();
	}
}
