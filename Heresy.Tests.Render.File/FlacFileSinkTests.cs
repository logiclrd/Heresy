using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Samples;
using Heresy.Render.File;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render.File;

[TestFixture]
public sealed class FlacFileSinkTests
{
	[Test]
	public void FlacIsTheDefaultLosslessOfflineFormat()
	{
		OfflineAudioFileFormats.DefaultLossless
			.Should().Be(OfflineAudioFileFormat.Flac);
		OfflineAudioFileFormats.GetDefaultExtension(
				OfflineAudioFileFormat.Flac)
			.Should().Be(".flac");
	}

	[Test]
	public void WritesCompletedBlocksBeforeCompleteAndRoundTripsPcm()
	{
		const int frames = 5000;
		AudioOutputFormat format =
			new(
				sampleRate: 8000,
				channelCount: 2);
		float[] pcm =
			new float[frames * format.ChannelCount];
		for (int frame = 0; frame < frames; frame++)
		{
			pcm[frame * 2] =
				((frame % 257) - 128) / 128.0f;
			pcm[(frame * 2) + 1] =
				(float)Math.Sin(
					frame * 0.03125);
		}

		using MemoryStream stream = new();
		using FlacFileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		sink.Write(pcm);

		// 42 bytes is the fLaC marker plus the mandatory STREAMINFO block.
		// A full 4096-frame block must already have been encoded rather than
		// keeping the whole song in memory until Complete().
		stream.Length.Should().BeGreaterThan(42);
		sink.FramesWritten.Should().Be(4096);

		sink.Complete();
		sink.FramesWritten.Should().Be(frames);

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				stream.ToArray(),
				"render.flac");
		decoded.SampleRate.Should().Be(format.SampleRate);
		decoded.ChannelCount.Should().Be(format.ChannelCount);
		decoded.FrameCount.Should().Be(frames);

		foreach (int frame in new[] { 0, 1, 4095, 4096, frames - 1 })
		{
			for (int channel = 0;
				channel < format.ChannelCount;
				channel++)
			{
				float expected =
					Quantize16(
						pcm[
							(frame * format.ChannelCount)
								+ channel]);
				decoded.GetSample(frame, channel)
					.Should().BeApproximately(
						expected,
						1.0f / 32768.0f);
			}
		}
	}

	[Test]
	public void ArbitraryWriteChunkingProducesOneContinuousStream()
	{
		AudioOutputFormat format =
			new(
				sampleRate: 44100,
				channelCount: 1);
		float[] pcm = new float[8205];
		for (int index = 0; index < pcm.Length; index++)
			pcm[index] = (index % 33) / 32.0f - 0.5f;

		using MemoryStream stream = new();
		using FlacFileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		int offset = 0;
		int[] chunkSizes = [1, 17, 4090, 3, 2048, 1024, 1022];
		foreach (int chunkSize in chunkSizes)
		{
			sink.Write(
				pcm.AsSpan(
					offset,
					chunkSize));
			offset += chunkSize;
		}
		offset.Should().Be(pcm.Length);

		sink.Complete();

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				stream.ToArray(),
				"chunks.flac");
		decoded.FrameCount.Should().Be(pcm.Length);
		for (int index = 0; index < pcm.Length; index += 997)
		{
			decoded.GetSample(index, 0)
				.Should().BeApproximately(
					Quantize16(pcm[index]),
					1.0f / 32768.0f);
		}
	}

	[Test]
	public void NonSeekableDestinationRemainsAValidStreamingFlac()
	{
		AudioOutputFormat format =
			new(
				sampleRate: 48000,
				channelCount: 1);
		using MemoryStream storage = new();
		using NonSeekableWriteStream stream =
			new(storage);
		using FlacFileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		sink.Write(
			[
				-1.0f,
				-0.5f,
				0.0f,
				0.5f,
				1.0f,
			]);
		sink.Complete();

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				storage.ToArray(),
				"stream.flac");
		decoded.FrameCount.Should().Be(5);
		decoded.GetSample(0, 0)
			.Should().BeApproximately(-1.0f, 1e-6f);
		decoded.GetSample(3, 0)
			.Should().BeApproximately(0.5f, 1.0f / 32768.0f);
	}

	[Test]
	public void RejectsNonFinitePcmAndMoreThanEightChannels()
	{
		using MemoryStream stream = new();

		Action tooManyChannels = () =>
		{
			using FlacFileSink _ =
				new(
					stream,
					new AudioOutputFormat(48000, 9),
					leaveOpen: true);
		};
		tooManyChannels.Should()
			.Throw<ArgumentOutOfRangeException>();

		using FlacFileSink sink =
			new(
				stream,
				new AudioOutputFormat(48000, 1),
				leaveOpen: true);
		Action nonFinite = () =>
			sink.Write([float.NaN]);
		nonFinite.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("*non-finite*");
	}

	private static float Quantize16(
		float value)
	{
		int sample =
			value <= -1.0f
				? short.MinValue
				: value >= 1.0f
					? short.MaxValue
					: (int)Math.Round(
						value * 32768.0,
						MidpointRounding.AwayFromZero);
		sample =
			Math.Clamp(
				sample,
				short.MinValue,
				short.MaxValue);
		return sample / 32768.0f;
	}

	private sealed class NonSeekableWriteStream : Stream
	{
		private readonly Stream _inner;

		public NonSeekableWriteStream(
			Stream inner)
			=> _inner =
				inner
					?? throw new ArgumentNullException(
						nameof(inner));

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length =>
			throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
			=> _inner.Flush();

		public override int Read(
			byte[] buffer,
			int offset,
			int count)
			=> throw new NotSupportedException();

		public override long Seek(
			long offset,
			SeekOrigin origin)
			=> throw new NotSupportedException();

		public override void SetLength(
			long value)
			=> throw new NotSupportedException();

		public override void Write(
			byte[] buffer,
			int offset,
			int count)
			=> _inner.Write(
				buffer,
				offset,
				count);

		public override void Write(
			ReadOnlySpan<byte> buffer)
			=> _inner.Write(buffer);

		protected override void Dispose(
			bool disposing)
		{
			// The wrapped storage is intentionally owned by the test.
			base.Dispose(disposing);
		}
	}
}
