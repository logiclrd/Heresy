using System;
using System.Buffers.Binary;
using System.IO;

using AwesomeAssertions;

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
	public void NonWaveAssetIsRejectedClearly()
	{
		Action act = () =>
			SampleAudioCodec.Decode(
				[0x66, 0x4C, 0x61, 0x43],
				"sample.flac");

		act.Should()
			.Throw<NotSupportedException>()
			.WithMessage("*WAVE*FLAC*");
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
