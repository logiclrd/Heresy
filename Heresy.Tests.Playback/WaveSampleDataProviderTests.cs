using System;
using System.Buffers.Binary;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class WaveSampleDataProviderTests
{
	[Test]
	public void DecodesPcm16StereoWave()
	{
		string path = WriteWave(
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

		try
		{
			WaveSampleDataProvider provider = new();
			ISampleData data =
				provider.GetSampleData(
					Sample(path));

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
		finally
		{
			File.Delete(path);
		}
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
		string path = WriteWave(
			formatTag: 3,
			channels: 1,
			sampleRate: 44100,
			bitsPerSample: 32,
			data: bytes);

		try
		{
			ISampleData data =
				new WaveSampleDataProvider()
					.GetSampleData(Sample(path));

			data.FrameCount.Should().Be(2);
			data.GetSample(0, 0)
				.Should().BeApproximately(0.25f, 1e-6f);
			data.GetSample(1, 0)
				.Should().BeApproximately(-0.5f, 1e-6f);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void ReusesDecodedDataForSameAssetIdentity()
	{
		string path = WriteWave(
			formatTag: 1,
			channels: 1,
			sampleRate: 8000,
			bitsPerSample: 8,
			data: [128]);

		try
		{
			WaveSampleDataProvider provider = new();
			SampleDefinition sample = Sample(path);

			ISampleData first =
				provider.GetSampleData(sample);
			ISampleData second =
				provider.GetSampleData(sample);

			second.Should().BeSameAs(first);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void NonWaveAssetIsRejectedClearly()
	{
		string path =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-{Guid.NewGuid():N}.flac");
		File.WriteAllBytes(
			path,
			[0x66, 0x4C, 0x61, 0x43]);

		try
		{
			Action act = () =>
				new WaveSampleDataProvider()
					.GetSampleData(Sample(path));

			act.Should()
				.Throw<NotSupportedException>()
				.WithMessage("*WAVE*");
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static SampleDefinition Sample(
		string path)
		=> new(
			(ObjectId)1U,
			"Sample",
			new ExternalAssetReference(path));

	private static string WriteWave(
		ushort formatTag,
		ushort channels,
		int sampleRate,
		ushort bitsPerSample,
		byte[] data)
	{
		string path =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-{Guid.NewGuid():N}.wav");

		using FileStream file =
			File.Create(path);
		using BinaryWriter writer =
			new(file);

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
		writer.Write(
			checked(
				36 + data.Length));
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

		return path;
	}
}
