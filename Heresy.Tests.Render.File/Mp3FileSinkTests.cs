using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Samples;
using Heresy.Render.File;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render.File;

[TestFixture]
public sealed class Mp3FileSinkTests
{
	[Test]
	public void Mp3IsAnOfflineFileFormat()
	{
		OfflineAudioFileFormats.GetDefaultExtension(
				OfflineAudioFileFormat.Mp3)
			.Should().Be(".mp3");
	}

	[Test]
	public void StreamsBeforeCompleteAndDecodesToAudiblePcm()
	{
		const int sampleRate = 44100;
		const int frames = sampleRate;
		AudioOutputFormat format =
			new(
				sampleRate,
				channelCount: 2);
		float[] pcm =
			new float[frames * format.ChannelCount];
		for (int frame = 0; frame < frames; frame++)
		{
			double time =
				frame / (double)sampleRate;
			pcm[frame * 2] =
				(float)(
					0.55
						* Math.Sin(
							2.0
								* Math.PI
								* 440.0
								* time));
			pcm[(frame * 2) + 1] =
				(float)(
					0.45
						* Math.Sin(
							2.0
								* Math.PI
								* 660.0
								* time));
		}

		using MemoryStream stream = new();
		using Mp3FileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		sink.Write(pcm);

		stream.Length.Should().BeGreaterThan(0);
		sink.FramesWritten.Should().Be(frames);

		sink.Complete();

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				stream.ToArray(),
				"render.mp3");
		decoded.SampleRate.Should().Be(sampleRate);
		decoded.ChannelCount.Should().Be(2);
		decoded.FrameCount.Should().BeGreaterThan(0);

		double peak = 0.0;
		for (int frame = 0;
			frame < decoded.FrameCount;
			frame += 97)
		{
			peak =
				Math.Max(
					peak,
					Math.Abs(
						decoded.GetSample(frame, 0)));
		}
		peak.Should().BeGreaterThan(0.2);
	}

	[Test]
	public void ArbitraryWholeFrameChunkingProducesOneContinuousStream()
	{
		const int sampleRate = 48000;
		const int frames = 16000;
		AudioOutputFormat format =
			new(
				sampleRate,
				channelCount: 2);
		float[] pcm =
			new float[frames * 2];
		for (int frame = 0; frame < frames; frame++)
		{
			float value =
				(float)(
					0.4
						* Math.Sin(
							frame * 0.071));
			pcm[frame * 2] = value;
			pcm[(frame * 2) + 1] = -value;
		}

		using MemoryStream stream = new();
		using Mp3FileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		int offsetFrames = 0;
		int[] chunkFrames =
			[1, 17, 2048, 4095, 123, 6000, 3716];
		foreach (int frameCount in chunkFrames)
		{
			sink.Write(
				pcm.AsSpan(
					offsetFrames * 2,
					frameCount * 2));
			offsetFrames += frameCount;
		}
		offsetFrames.Should().Be(frames);
		sink.FramesWritten.Should().Be(frames);

		sink.Complete();

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				stream.ToArray(),
				"chunks.mp3");
		decoded.SampleRate.Should().Be(sampleRate);
		decoded.ChannelCount.Should().Be(2);
		decoded.FrameCount.Should().BeGreaterThan(0);
	}

	[Test]
	public void NonSeekableDestinationProducesAValidMp3()
	{
		const int sampleRate = 48000;
		const int frames = 12000;
		AudioOutputFormat format =
			new(
				sampleRate,
				channelCount: 1);
		float[] pcm = new float[frames];
		for (int frame = 0; frame < frames; frame++)
		{
			pcm[frame] =
				(float)(
					0.5
						* Math.Sin(
							frame * 0.05));
		}

		using MemoryStream storage = new();
		using NonSeekableWriteStream stream =
			new(storage);
		using Mp3FileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		sink.Write(pcm);
		sink.Complete();

		SamplePcmData decoded =
			SampleAudioCodec.Decode(
				storage.ToArray(),
				"stream.mp3");
		decoded.SampleRate.Should().Be(sampleRate);
		decoded.ChannelCount.Should().Be(1);
		decoded.FrameCount.Should().BeGreaterThan(0);
	}

	[Test]
	public void RejectsNonFinitePcmAndMoreThanStereo()
	{
		using MemoryStream stream = new();

		Action tooManyChannels = () =>
		{
			using Mp3FileSink _ =
				new(
					stream,
					new AudioOutputFormat(48000, 3),
					leaveOpen: true);
		};
		tooManyChannels.Should()
			.Throw<ArgumentOutOfRangeException>();

		using Mp3FileSink sink =
			new(
				stream,
				new AudioOutputFormat(48000, 1),
				leaveOpen: true);
		Action nonFinite = () =>
			sink.Write([float.PositiveInfinity]);
		nonFinite.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("*non-finite*");
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
			base.Dispose(disposing);
		}
	}
}
