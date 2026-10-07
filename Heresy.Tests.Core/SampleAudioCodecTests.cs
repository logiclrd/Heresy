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
		"ZkxhQwAAACIEgASAAAJyAAJyAfQA8AAAAoAARtjXQTF9MY5lvAy705J8AwAAEgAAAAAAAAAAAAAAAAAAAAACgIQAACggAAAAcmVmZXJlbmNlIGxpYkZMQUMgMS41LjAgMjAyNTAyMTEAAAAA//h0CAACf8UYAAAFawoyDcUBkRsZnZoROELKgsbW6r6vlj4vTU0OTYncJ2Huys7e4pu+re9Ljg2OCp2qV+6u2uLOu9a9TsrNTk4KiROj9VNnb3NX/353BQaHB0XEDNL1pbS2uLCg5YmhSXHZyXkzJG2oLWyurOh74OZCZGx2YFjNIw+WlncWtN3zcXhmaHByTuFa75W29rb1PrTseFhucmhe7WKfVVaXFvU0ebI7KjQ4OiwgYJu9PZ29zW/+WZsTGpucmJCzQ86ituriupeOLuQmxmdmhE4QsqCxtbqvq+WPi9NTQ5NidwnYe7Kzt7im76t70uODY4KnapX7q7a4s671r1Oys1OTgqJE6P1U2dvc1f/fncFBocHRcQM0vWltLa4sKDliaFJcdnJeTMkbagtbK6s6Hvg5kJkbHZgWM0jD5aWdxa03fNxeGZocHJO4Vrvlbb2tvU+tOx4WG5yaF7tYp9VVpcW9TR5sjsqNDg6LCBgm709nb3Nb/5ZmxMam5yYkLNDzqK26uK6l44u5CbGZ2aEThCyoLG1uq+r5Y+L01NDk2J3Cdh7srO3uKbvq3vS44NjgqdqlfurtrizrvWvU7KzU5OCokTo/VTZ29zV/9+dwUGhwdFxAzS9aW0triwoOWJoUlx2cl5MyRtqC1srqzoe+DmQmRsdmBYzSMPlpZ3FrTd83F4Zmhwck7hWu+Vtva29T607HhYbnJoXu1in1VWlxb1NHmyOyo0ODosIGCbvT2dvc1v/lmbExqbnJiQs0POorbq4rqXji7kJsZnZoROELKgsbW6r6vlj4vTU0OTYncJ2Huys7e4pu+reAv2U=";

	private const string Mp3Fixture =
		"/+NIxAA5Yv5QAVoYAUA6Acu+XLLloB0V0x1jqCJCIBC5BcgvAg4oIsRU6EstmWTLJlx23vJzgIIkaZVecWyenqfv2fXWdmGakWpqydeBdwvAg4po1yWZyt/3Lct/43b05hAAAAEocAEbuegABgb/XDgYAAIiIgAAAYGLd3cDAwAAEIiBAAAAwMDd3DgYGAAAAiIEAAADAwMW7hwMDAAAAEIgQAAAMDAxbu4GLAAAAREQIIAwMW7u7iwAAREREQW7u7u7iEAAAAAw8PDw8AAAAAAw8PDw8AAAAAAw8PDw8AAAAAAw8PDw8AAAAAAw8PDw8AAFAINAIJGCoLGEIYGAQM75lqAV13F/GDwRmGAjmBoXGHYlZVd0qDj2EwEmBIHm/+NIxC1F28JUKZ3QADiUBhULp98yxqSeE0YHAAfhrIY8AcJAOYPgWevYGZWiSZR+b5qbBqASRlSBkSmXcceaOmhBBQSIApYFQKXYWBmEBmCDo3Ftiyy0UVf////8OHoYL0QMTnbdLtciElkSplAnRXau19mdM6//////9miBic7bpdqYPIpBc8DxFyXJiT/P9KX9f2ajUa////////Zu4Egbx259v3YnHkdufqxmW1qamq0tnVNlvH/////////b9rE45jlz73uReexy78Hu5zWWf46/6uvrY41csv///////////70HO/2FP5yQv/lIoc3LYf1LpMoAQCEQiERACAMlAQcAzAYQytTO///CoFKwgYSF5hQJWZrkNf/+YzAR/+NIxChEo3JceZzQAZpkR6/iHPyRH+S5VX//zr9jXjzYAzLLQMgBSppLyw1WtTP///5yfRxzZ0i5tL5tCRwRhq5xqR+7NbWVXeP////+bYKaKGZ4YArxnXRmRYKmmYZmSEBkatrKrvGtrKr//////5l1xjxIk9MkpMQIGmxk0hhRJExMgjMEIKy+8a2squ8a2sqv///////5jzwBEkSgxh0CCB5OY00DRIsgMUZCggSPmLLBcTvGtrKrvlbXau+f////////4cYMUVEQgMLmJJCokIKGIJjAoHEzEkCEWCiBhh5IKBQ8w44sC62squ8a2u1d8ra7VmpMQU1FMy4xMDCqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq/+NIxAAAAANIAcAAAExBTUUzLjEwMKqqqqqqqqqqqqqqTEFNRTMuMTAwqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";

	private const string OggVorbisFixture =
		"T2dnUwACAAAAAAAAAAB3nfr4AAAAAHR9wSQBHgF2b3JiaXMAAAAAAUAfAAAAAAAAkEwAAAAAAACZAU9nZ1MAAAAAAAAAAAAAd536+AEAAABdvwjRCz////////////+1A3ZvcmJpcwwAAABMYXZmNjEuNy4xMDMBAAAAHwAAAGVuY29kZXI9TGF2YzYxLjE5LjEwMSBsaWJ2b3JiaXMBBXZvcmJpcxJCQ1YBAAABAAxSFCElGVNKYwiVUlIpBR1jUFtHHWPUOUYhZBBTiEkZpXtPKpVYSsgRUlgpRR1TTFNJlVKWKUUdYxRTSCFT1jFloXMUS4ZJCSVsTa50FkvomWOWMUYdY85aSp1j1jFFHWNSUkmhcxg6ZiVkFDpGxehifDA6laJCKL7H3lLpLYWKW4q91xpT6y2EGEtpwQhhc+211dxKasUYY4wxxsXiUyiC0JBVAAABAABABAFCQ1YBAAoAAMJQDEVRgNCQVQBABgCAABRFcRTHcRxHkiTLAkJDVgEAQAAAAgAAKI7hKJIjSZJkWZZlWZameZaouaov+64u667t6roOhIasBADIAAAYhiGH3knMkFOQSSYpVcw5CKH1DjnlFGTSUsaYYoxRzpBTDDEFMYbQKYUQ1E45pQwiCENInWTOIEs96OBi5zgQGrIiAIgCAACMQYwhxpBzDEoGIXKOScggRM45KZ2UTEoorbSWSQktldYi55yUTkompbQWUsuklNZCKwUAAAQ4AAAEWAiFhqwIAKIAABCDkFJIKcSUYk4xh5RSjinHkFLMOcWYcowx6CBUzDHIHIRIKcUYc0455iBkDCrmHIQMMgEAAAEOAAABFkKhISsCgDgBAIMkaZqlaaJoaZooeqaoqqIoqqrleabpmaaqeqKpqqaquq6pqq5seZ5peqaoqp4pqqqpqq5rqqrriqpqy6ar2rbpqrbsyrJuu7Ks256qyrapurJuqq5tu7Js664s27rkearqmabreqbpuqrr2rLqurLtmabriqor26bryrLryratyrKua6bpuqKr2q6purLtyq5tu7Ks+6br6rbqyrquyrLu27au+7KtC7vourauyq6uq7Ks67It67Zs20LJ81TVM03X9UzTdVXXtW3VdW1bM03XNV1XlkXVdWXVlXVddWVb90zTdU1XlWXTVWVZlWXddmVXl0XXtW1Vln1ddWVfl23d92VZ133TdXVblWXbV2VZ92Vd94VZt33dU1VbN11X103X1X1b131htm3fF11X11XZ1oVVlnXf1n1lmHWdMLqurqu27OuqLOu+ruvGMOu6MKy6bfyurQvDq+vGseu+rty+j2rbvvDqtjG8um4cu7Abv+37xrGpqm2brqvrpivrumzrvm/runGMrqvrqiz7uurKvm/ruvDrvi8Mo+vquirLurDasq/Lui4Mu64bw2rbwu7aunDMsi4Mt+8rx68LQ9W2heHVdaOr28ZvC8PSN3a+AACAAQcAgAATykChISsCgDgBAAYhCBVjECrGIIQQUgohpFQxBiFjDkrGHJQQSkkhlNIqxiBkjknIHJMQSmiplNBKKKWlUEpLoZTWUmotptRaDKG0FEpprZTSWmopttRSbBVjEDLnpGSOSSiltFZKaSlzTErGoKQOQiqlpNJKSa1lzknJoKPSOUippNJSSam1UEproZTWSkqxpdJKba3FGkppLaTSWkmptdRSba21WiPGIGSMQcmck1JKSamU0lrmnJQOOiqZg5JKKamVklKsmJPSQSglg4xKSaW1kkoroZTWSkqxhVJaa63VmFJLNZSSWkmpxVBKa621GlMrNYVQUgultBZKaa21VmtqLbZQQmuhpBZLKjG1FmNtrcUYSmmtpBJbKanFFluNrbVYU0s1lpJibK3V2EotOdZaa0ot1tJSjK21mFtMucVYaw0ltBZKaa2U0lpKrcXWWq2hlNZKKrGVklpsrdXYWow1lNJiKSm1kEpsrbVYW2w1ppZibLHVWFKLMcZYc0u11ZRai621WEsrNcYYa2415VIAAMCAAwBAgAlloNCQlQBAFAAAYAxjjEFoFHLMOSmNUs45JyVzDkIIKWXOQQghpc45CKW01DkHoZSUQikppRRbKCWl1losAACgwAEAIMAGTYnFAQoNWQkARAEAIMYoxRiExiClGIPQGKMUYxAqpRhzDkKlFGPOQcgYc85BKRljzkEnJYQQQimlhBBCKKWUAgAAChwAAAJs0JRYHKDQkBUBQBQAAGAMYgwxhiB0UjopEYRMSielkRJaCylllkqKJcbMWomtxNhICa2F1jJrJcbSYkatxFhiKgAA7MABAOzAQig0ZCUAkAcAQBijFGPOOWcQYsw5CCE0CDHmHIQQKsaccw5CCBVjzjkHIYTOOecghBBC55xzEEIIoYMQQgillNJBCCGEUkrpIIQQQimldBBCCKGUUgoAACpwAAAIsFFkc4KRoEJDVgIAeQAAgDFKOSclpUYpxiCkFFujFGMQUmqtYgxCSq3FWDEGIaXWYuwgpNRajLV2EFJqLcZaQ0qtxVhrziGl1mKsNdfUWoy15tx7ai3GWnPOuQAA3AUHALADG0U2JxgJKjRkJQCQBwBAIKQUY4w5h5RijDHnnENKMcaYc84pxhhzzjnnFGOMOeecc4wx55xzzjnGmHPOOeecc84556CDkDnnnHPQQeicc845CCF0zjnnHIQQCgAAKnAAAAiwUWRzgpGgkNWAgDhAACAMZRSSimllFJKqKOUUkoppZRSAiGllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimllFJKKaWUUkoppZRSSimVUkoppZRSSimllFJKKaUAIN8KBwD/BxtnWEk6KxwNLjRkJQAQDgAAGMMYhIw5JyWlhjEIpXROSkklNYxBKKVzElJKKYPQWmqlpNJSShmElGILIZWUWgqltFZrKam1lFIoKcUaS0qppdYy5ySkklpLrbaYOQelpNZaaq3FEEJKsbXWUmuxdVJSSa211lptLaSUWmstxtZibCWlllprqcXWWkyptRZbSy3G1mJLrcXYYosxxhoLAOBucACASLBxhpWks8LR4EJDVgIAIQEABDJKOeecgxBCCCFSijHnoIMQQgghREox5pyDEEIIIYSMMecghBBCCKGUkDHmHIQQQgghhFI65yCEUEoJpZRSSucchBBCCKWUUkoJIYQQQiillFJKKSGEEEoppZRSSiklhBBCKKWUUkoppYQQQiillFJKKaWUEEIopZRSSimllBJCCKGUUkoppZRSQgillFJKKaWUUkooIYRSSimllFJKCSWUUkoppZRSSikhlFJKKaWUUkoppQAAgAMHAIAAI+gko8oibDThwgMQAAAAAgACTACBAYKCUQgChBEIAAAAAAAIAPgAAEgKgIiIaOYMDhASFBYYGhweICIkAAAAAAAAAAAAAAAABE9nZ1MABIACAAAAAAAAd536+AIAAAAs2jHfBBUSEyOSlpl54RUAvEcAAAAppDFhTGg2qyOal5lzJ68A4KkAAAAAAI1+mQKal5ndySsAUAUAAACsaZo2UjMVgspQseQVAOwUAAMARJE/iqKrZ1cSYEA/HsrMzMzMzHzikwA=";

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
		const int sampleRate = 8000;
		byte[] encoded =
			Convert.FromBase64String(Mp3Fixture);

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
