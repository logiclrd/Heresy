using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

using Heresy.Render.File;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render.File;

[TestFixture]
public sealed class WavePcmBitDepthTests
{
	[TestCase(WavePcmBitDepth.Pcm8, 1)]
	[TestCase(WavePcmBitDepth.Pcm16, 2)]
	[TestCase(WavePcmBitDepth.Pcm24, 3)]
	[TestCase(WavePcmBitDepth.Pcm32, 4)]
	public void IntegerPcmQuantizationHeadersAndSignedEndianEncoding(
		WavePcmBitDepth depth, int bytesPerSample)
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream,
			new AudioOutputFormat(48000, 1), leaveOpen: true, bitDepth: depth);
		sink.Write([-1f, -0.5f, 0f, 0.5f, 1f, 2f]);
		sink.Complete();
		byte[] bytes = stream.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(bytes.Length, Is.EqualTo(44 + 6 * bytesPerSample));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(4, 4)), Is.EqualTo((uint)(bytes.Length - 8)));
			Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(
					bytes.AsSpan(20, 2)), Is.EqualTo(1)); // PCM, not float
			Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(
					bytes.AsSpan(34, 2)), Is.EqualTo((ushort)depth));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(28, 4)), Is.EqualTo((uint)(48000 * bytesPerSample)));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(40, 4)), Is.EqualTo((uint)(6 * bytesPerSample)));
			Assert.That(sink.FramesWritten, Is.EqualTo(6));
		});
		if (depth == WavePcmBitDepth.Pcm8)
		{
			Assert.That(bytes.Skip(44).ToArray(),
				Is.EqualTo(new byte[] { 0, 64, 128, 192, 255, 255 }));
		}
		else if (depth == WavePcmBitDepth.Pcm16)
		{
			short[] expected = [-32768, -16384, 0, 16384, 32767, 32767];
			for (int i = 0; i < expected.Length; i++)
				Assert.That(BinaryPrimitives.ReadInt16LittleEndian(
					bytes.AsSpan(44 + i * 2, 2)), Is.EqualTo(expected[i]));
		}
		else if (depth == WavePcmBitDepth.Pcm24)
		{
			byte[] expected =
			[
				0, 0, 128, 0, 0, 192, 0, 0, 0,
				0, 0, 64, 255, 255, 127, 255, 255, 127,
			];
			Assert.That(bytes.Skip(44).ToArray(), Is.EqualTo(expected));
		}
		else
		{
			int[] expected = [int.MinValue, -1073741824, 0, 1073741824,
				int.MaxValue, int.MaxValue];
			for (int i = 0; i < expected.Length; i++)
				Assert.That(BinaryPrimitives.ReadInt32LittleEndian(
					bytes.AsSpan(44 + i * 4, 4)), Is.EqualTo(expected[i]));
		}
	}

	[Test]
	public void OddLengthEightBitDataAddsPaddingWithoutInflatingDataChunkSize()
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream, new AudioOutputFormat(8000, 1),
			leaveOpen: true, bitDepth: WavePcmBitDepth.Pcm8);
		sink.Write([0f, 0.5f, -0.5f]);
		sink.Complete();
		sink.Complete(); // No duplicate pad on repeated completion
		byte[] bytes = stream.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(bytes.Length, Is.EqualTo(48));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(4, 4)), Is.EqualTo(40));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(40, 4)), Is.EqualTo(3));
			Assert.That(bytes.AsSpan(44, 4).ToArray(),
					Is.EqualTo(new byte[] { 128, 192, 64, 0 }));
		});
	}

	[TestCase(6, 0x3f)]
	[TestCase(8, 0x63f)]
	public void SurroundWavUsesExtensiblePcmHeaderWithSpeakerOrderMask(
		int channelCount, int expectedMask)
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream,
			new AudioOutputFormat(48000, channelCount),
			leaveOpen: true, bitDepth: WavePcmBitDepth.Pcm24);
		sink.Write(new float[channelCount]);
		sink.Complete();
		byte[] bytes = stream.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(bytes.Length, Is.EqualTo(68 + 3 * channelCount));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(16, 4)), Is.EqualTo(40));
			Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(
					bytes.AsSpan(20, 2)), Is.EqualTo(0xfffe));
			Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(
					bytes.AsSpan(36, 2)), Is.EqualTo(22));
			Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(
					bytes.AsSpan(38, 2)), Is.EqualTo(24));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(40, 4)), Is.EqualTo((uint)expectedMask));
			Assert.That(bytes.AsSpan(44, 16).ToArray(),
					Is.EqualTo(new byte[]
					{
						1, 0, 0, 0, 0, 0, 16, 0,
						128, 0, 0, 170, 0, 56, 155, 113,
					}));
			Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 60, 4),
					Is.EqualTo("data"));
			Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.AsSpan(64, 4)), Is.EqualTo((uint)(3 * channelCount)));
		});
	}

	[Test]
	public void NonfiniteInputIsRejectedWithoutPartialPcmWrite()
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream, new AudioOutputFormat(48000, 1),
			leaveOpen: true, bitDepth: WavePcmBitDepth.Pcm24);
		Assert.Throws<InvalidOperationException>(() => sink.Write([0f, float.NaN]));
		Assert.That(sink.FramesWritten, Is.Zero);
		Assert.That(stream.Length, Is.EqualTo(44));
	}

	[Test]
	public void RejectsUndefinedWavDepth()
	{
		using MemoryStream stream = new();
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new WaveFileSink(stream, new AudioOutputFormat(48000, 2),
				leaveOpen: true, bitDepth: (WavePcmBitDepth)20));
	}

	[Test]
	public void ExportFormatValidationRejectsUnsupportedRatesAndLayouts()
	{
		Assert.Multiple(() =>
		{
			Assert.Throws<ArgumentException>(() =>
				OfflineAudioFileFormats.ValidateOutput(
					OfflineAudioFileFormat.Mp3, new AudioOutputFormat(96000, 2)));
			Assert.Throws<ArgumentException>(() =>
				OfflineAudioFileFormats.ValidateOutput(
					OfflineAudioFileFormat.Mp3, new AudioOutputFormat(48000, 6)));
			Assert.Throws<ArgumentException>(() =>
				OfflineAudioFileFormats.ValidateOutput(
					OfflineAudioFileFormat.Flac, new AudioOutputFormat(48000, 9)));
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				OfflineAudioFileFormats.ValidateOutput(
					OfflineAudioFileFormat.Wave, new AudioOutputFormat(48000, 2),
					(WavePcmBitDepth)12));
			Assert.Throws<ArgumentException>(() =>
				OfflineAudioFileFormats.ValidateOutput(
					OfflineAudioFileFormat.Flac, new AudioOutputFormat(48000, 2),
					WavePcmBitDepth.Pcm24));
		});
		foreach (int rate in new[] { 8000, 11025, 12000, 16000,
			22050, 24000, 32000, 44100, 48000 })
			Assert.DoesNotThrow(() => OfflineAudioFileFormats.ValidateOutput(
				OfflineAudioFileFormat.Mp3, new AudioOutputFormat(rate, 2)));
		Assert.DoesNotThrow(() => OfflineAudioFileFormats.ValidateOutput(
			OfflineAudioFileFormat.Wave, new AudioOutputFormat(384000, 8),
			WavePcmBitDepth.Pcm32));
	}
}
