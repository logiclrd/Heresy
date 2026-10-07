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
	private const string FlacFixture =
		"ZkxhQwAAACIQABAAAAE6AAE6AfQA8AAAAoAARtjXQTF9MY5lvAy705J8AwAAEgAAAAAAAAAAAAAAAAAAAAACgIQAACggAAAAcmVmZXJlbmNlIGxpYkZMQUMgMS41LjAgMjAyNTAyMTEAAAAA//h0CAACf8VIAAAFawoyDcUPtrSvrV/4gdZ0GAIqAkIJCU4bsZFhCIJBUQbIb8UIIhERejb+SEgIiFJG4+5BCgQkKs5tloJCEEi0g5nOQkCEiBeRvONLAhYIRE0z3+RQQhESpwftUEJBEiUbQ3NIgkIQso5s7KgJCCQlOG7GRYQiCQVEGyG/FCCIREXo2/khICIhSRuPuQQoEJCrObZaCQhBItIOZzkJAhIgXkbzjSwIWCERNM9/kUEIREqcH7VBCQRIlG0NzSIJCELKObOyoCQgkJThuxkWEIgkFRBshvxQgiERF6Nv5ISAiIUkbj7kEKBCQqzm2WgkIQSLSDmc5CQISIF5G840sCFghETTPf5FBCERKnB+1QQkESJRtDc0iCQhCyjmzsqAkIJCU4bsZFhCIJBUQbIbtz0=";


	private const string OggVorbisFixture =
		"T2dnUwACAAAAAAAAAABcqbAwAAAAAAQUf2sBHgF2b3JiaXMAAAAAAUAfAAAAAAAAcGIAAAAAAACZAU9nZ1MAAAAAAAAAAAAAXKmwMAEAAABL+3QQCz////////////+1A3ZvcmJpcwwAAABMYXZmNjEuNy4xMDMBAAAAHwAAAGVuY29kZXI9TGF2YzYxLjE5LjEwMSBsaWJ2b3JiaXMBBXZvcmJpcxJCQ1YBAAABAAxSFCElGVNKYwiVUlIpBR1jUFtHHWPUOUYhZBBTiEkZpXtPKpVYSsgRUlgpRR1TTFNJlVKWKUUdYxRTSCFT1jFloXMUS4ZJCSVsTa50FkvomWOWMUYdY85aSp1j1jFFHWNSUkmhcxg6ZiVkFDpGxehifDA6laJCKL7H3lLpLYWKW4q91xpT6y2EGEtpwQhhc+211dxKasUYY4wxxsXiUyiC0JBVAAABAABABAFCQ1YBAAoAAMJQDEVRgNCQVQBABgCAABRFcRTHcRxHkiTLAkJDVgEAQAAAAgAAKI7hKJIjSZJkWZZlWZameZaouaov+64u667t6roOhIasBADIAAAYhiGH3knMkFOQSSYpVcw5CKH1DjnlFGTSUsaYYoxRzpBTDDEFMYbQKYUQ1E45pQwiCENInWTOIEs96OBi5zgQGrIiAIgCAACMQYwhxpBzDEoGIXKOScggRM45KZ2UTEoorbSWSQktldYi55yUTkompbQWUsuklNZCKwUAAAQ4AAAEWAiFhqwIAKIAABCDkFJIKcSUYk4xh5RSjinHkFLMOcWYcowx6CBUzDHIHIRIKcUYc0455iBkDCrmHIQMMgEAAAEOAAABFkKhISsCgDgBAIMkaZqlaaJoaZooeqaoqqIoqqrleabpmaaqeqKpqqaquq6pqq5seZ5peqaoqp4pqqqpqq5rqqrriqpqy6ar2rbpqrbsyrJuu7Ks256qyrapurJuqq5tu7Js664s27rkearqmabreqbpuqrr2rLqurLtmabriqor26bryrLryratyrKua6bpuqKr2q6purLtyq5tu7Ks+6br6rbqyrquyrLu27au+7KtC7vourauyq6uq7Ks67It67Zs20LJ81TVM03X9UzTdVXXtW3VdW1bM03XNV1XlkXVdWXVlXVddWVb90zTdU1XlWXTVWVZlWXddmVXl0XXtW1Vln1ddWVfl23d92VZ133TdXVblWXbV2VZ92Vd94VZt33dU1VbN11X103X1X1b131htm3fF11X11XZ1oVVlnXf1n1lmHWdMLqurqu27OuqLOu+ruvGMOu6MKy6bfyurQvDq+vGseu+rty+j2rbvvDqtjG8um4cu7Abv+37xrGpqm2brqvrpivrumzrvm/runGMrqvrqiz7uurKvm/ruvDrvi8Mo+vquirLurDasq/Lui4Mu64bw2rbwu7aunDMsi4Mt+8rx68LQ9W2heHVdaOr28ZvC8PSN3a+AACAAQcAgAATykChISsCgDgBAAYhCBVjECrGIIQQUgohpFQxBiFjDkrGHJQQSkkhlNIqxiBkjknIHJMQSmiplNBKKKWlUEpLoZTWUmotptRaDKG0FEpprZTSWmopttRSbBVjEDLnpGSOSSiltFZKaSlzTErGoKQOQiqlpNJKSa1lzknJoKPSOUippNJSSam1UEproZTWSkqxpdJKba3FGkppLaTSWkmptdRSba21WiPGIGSMQcmck1JKSamU0lrmnJQOOiqZg5JKKamVklKsmJPSQSglg4xKSaW1kkoroZTWSkqxhVJaa63VmFJLNZSSWkmpxVBKa621GlMrNYVQUgultBZKaa21VmtqLbZQQmuhpBZLKjG1FmNtrcUYSmmtpBJbKanFFluNrbVYU0s1lpJibK3V2EotOdZaa0ot1tJSjK21mFtMucVYaw0ltBZKaa2U0lpKrcXWWq2hlNZKKrGVklpsrdXYWow1lNJiKSm1kEpsrbVYW2w1ppZibLHVWFKLMcZYc0u11ZRai621WEsrNcYYa2415VIAAMCAAwBAgAlloNCQlQBAFAAAYAxjjEFoFHLMOSmNUs45JyVzDkIIKWXOQQghpc45CKW01DkHoZSUQikppRRbKCWl1losAACgwAEAIMAGTYnFAQoNWQkARAEAIMYoxRiExiClGIPQGKMUYxAqpRhzDkKlFGPOQcgYc85BKRljzkEnJYQQQimlhBBCKKWUAgAAChwAAAJs0JRYHKDQkBUBQBQAAGAMYgwxhiB0UjopEYRMSielkRJaCylllkqKJcbMWomtxNhICa2F1jJrJcbSYkatxFhiKgAA7MABAOzAQig0ZCUAkAcAQBijFGPOOWcQYsw5CCE0CDHmHIQQKsaccw5CCBVjzjkHIYTOOecghBBC55xzEEIIoYMQQgillNJBCCGEUkrpIIQQQimldBBCCKGUUgoAACpwAAAIsFFkc4KRoEJDVgIAeQAAgDFKOSclpUYpxiCkFFujFGMQUmqtYgxCSq3FWDEGIaXWYuwgpNRajLV2EFJqLcZaQ0qtxVhrziGl1mKsNdfUWoy15tx7ai3GWnPOuQAA3AUHALADG0U2JxgJKjRkJQCQBwBAIKQUY4w5h5RijDHnnENKMcaYc84pxhhzzjnnFGOMOeecc4wx55xzzjnGmHPOOeecc84556CDkDnnnHPQQeicc845CCF0zjnnHIQQCgAAKnAAAAiwUWRzgpGgQkNWAgDhAACAMZRSSimllFJKqKOUUkoppZRSAiGllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimVUkoppZRSSimllFJKKaUAIN8KBwD/BxtnWEk6KxwNLjRkJQAQDgAAGMMYhIw5JyWlhjEIpXROSkklNYxBKKVzElJKKYPQWmqlpNJSShmElGILIZWUWgqltFZrKam1lFIoKcUaS0qppdYy5ySkklpLrbaYOQelpNZaaq3FEEJKsbXWUmuxdVJSSa211lptLaSUWmstxtZibCWlllprqcXWWkyptRZbSy3G1mJLrcXYYosxxhoLAOBucACASLBxhpWks8LR4EJDVgIAIQEABDJKOeecgxBCCCFSijHnoIMQQgghREox5pyDEEIIIYSMMecghBBCCKGUkDHmHIQQQgghhFI65yCEUEoJpZRSSucchBBCCKWUUkoJIYQQQiillFJKKSGEEEoppZRSSiklhBBCKKWUUkoppYQQQiillFJKKaWUEEIopZRSSimllBJCCKGUUkoppZRSQgillFJKKaWUUkooIYRSSimllFJKCSWUUkoppZRSSikhlFJKKaWUUkoppQAAgAMHAIAAI+gko8oibDThwgMQAAAAAgACTACBAYKCUQgChBEIAAAAAAAIAPgAAEgKgIiIaOYMDhASFBYYGhweICIkAAAAAAAAAAAAAAAABE9nZ1MABIACAAAAAAAAXKmwMAIAAABnDF8+BBgWGCaGlJlZqVcA8E93AQAAQioJr5S702cyMwKOlZndTeYBAAAAABzLAACECm+pPY8BjpSZs5vMAwAAAABQBgC2XVa02rWG2YwAdspQseYVAOb9AAAGAELU6Gh1zKcztjPgGYrxeJyZmZmZmfnEJwE=";

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
		byte[] encoded =
			Convert.FromBase64String(FlacFixture);

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.flac");

		data.SampleRate.Should().Be(8000);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().BeGreaterThan(0);
		PeakMagnitude(data).Should().BeGreaterThan(0.05f);
	}

	[Test]
	public void DecodesMp3AtLoadTime()
	{
		byte[] encoded =
			BuildLayerIIMonoOneActiveSubbandFrame();

		SamplePcmData data =
			SampleAudioCodec.Decode(
				encoded,
				"sample.mp3");

		data.SampleRate.Should().Be(48000);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().BeGreaterThan(0);
		PeakMagnitude(data).Should().BeGreaterThan(0.0001f);
	}

	[Test]
	public void DecodesOggVorbisAtLoadTime()
	{
		const int sampleRate = 8000;
		byte[] encoded =
			Convert.FromBase64String(OggVorbisFixture);

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

	private static byte[] BuildLayerIIMonoOneActiveSubbandFrame()
	{
		byte[] frame = new byte[384];
		frame[0] = 0xFF;
		frame[1] = 0xFD;
		frame[2] = 0x84;
		frame[3] = 0xC0;

		BitWriter writer =
			new(
				frame,
				startBit: 32);

		writer.Write(1, 4);
		writer.Write(0, 4);
		writer.Write(0, 4);
		for (int index = 0; index < 8; index++)
			writer.Write(0, 4);
		for (int index = 0; index < 12; index++)
			writer.Write(0, 3);
		for (int index = 0; index < 4; index++)
			writer.Write(0, 2);

		writer.Write(0, 2);
		writer.Write(10, 6);
		writer.Write(10, 6);
		writer.Write(10, 6);
		for (int group = 0; group < 4; group++)
			writer.Write(13, 5);

		return frame;
	}

	private sealed class BitWriter
	{
		private readonly byte[] _buffer;
		private int _bitPosition;

		public BitWriter(
			byte[] buffer,
			int startBit)
		{
			_buffer = buffer;
			_bitPosition = startBit;
		}

		public void Write(
			int value,
			int bits)
		{
			for (int index = bits - 1; index >= 0; index--)
			{
				int bit =
					(value >> index) & 1;
				if (bit != 0)
				{
					_buffer[_bitPosition >> 3] |=
						(byte)(0x80 >> (_bitPosition & 7));
				}
				_bitPosition++;
			}
		}
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
