using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;

using NLayer;

using NUnit.Framework;

namespace Heresy.Tests.Core;

/// <summary>
/// Actual MPEG-1 Layer III stream encoded offline by FFmpeg/libmp3lame at
/// 44.1 kHz mono, 48 kb/s CBR, -write_xing 0, -id3v2_version 0.
/// Its waveform is a continuous, fade-windowed sum of three sines
/// (437, 1139 and 2291 Hz); no audio encoder or native codec runs in CI.
/// File provenance and ffmpeg command are recorded in sample-storage.md.
/// </summary>
[TestFixture]
public sealed class SampleAudioCodecLayer3Tests
{
	private static byte[] Fixture()
		=> File.ReadAllBytes(Path.Combine(
			TestContext.CurrentContext.TestDirectory, "Fixtures",
			"layer3-continuous-mono.mp3"));

	[Test]
	public void ActualFixtureHasMultipleConsecutiveMpeg1Layer3Frames()
	{
		byte[] encoded = Fixture();
		int offset = 0;
		int frames = 0;
		while (offset + 4 <= encoded.Length)
		{
			// MPEG-1, Layer III, 44.1 kHz, 48 kb/s CBR.
			encoded[offset].Should().Be(0xFF);
			(encoded[offset + 1] & 0xFE).Should().Be(0xFA,
				"MPEG-1 Layer III frame synchronisation must be present");
			int bitrateIndex = encoded[offset + 2] >> 4;
			bitrateIndex.Should().Be(3); // 48 kb/s MPEG-1 Layer III
			int rateIndex = (encoded[offset + 2] >> 2) & 3;
			rateIndex.Should().Be(0); // 44.1 kHz
			int padded = (encoded[offset + 2] >> 1) & 1;
			offset += 144 * 48000 / 44100 + padded;
			frames++;
		}
		offset.Should().Be(encoded.Length,
			"the fixture consists exclusively of complete Layer III frames");
		frames.Should().BeGreaterThan(12);
	}

	[Test]
	public void ImportedLayer3IsContinuousAndHasNoRecurringFrameBoundaryChirps()
	{
		SamplePcmData data = SampleAudioCodec.Decode(
			Fixture(), "layer3-continuous-mono.mp3");

		data.SampleRate.Should().Be(44100);
		data.ChannelCount.Should().Be(1);
		data.FrameCount.Should().BeGreaterThan(15000);
		float peak = 0;
		float maximumStep = 0;
		float boundaryStep = 0;
		double sumSteps = 0;
		for (long frame = 0; frame < data.FrameCount; frame++)
		{
			float sample = data.GetSample(frame, 0);
			float.IsFinite(sample).Should().BeTrue();
			peak = Math.Max(peak, Math.Abs(sample));
			if (frame == 0)
				continue;
			float step = Math.Abs(sample - data.GetSample(frame - 1, 0));
			maximumStep = Math.Max(maximumStep, step);
			sumSteps += step;
			if (frame % 1152 == 0)
				boundaryStep = Math.Max(boundaryStep, step);
		}
		peak.Should().BeGreaterThan(0.15f);
		peak.Should().BeLessThan(0.9f);
		(sumSteps / (data.FrameCount - 1)).Should().BeGreaterThan(0.003);
		// The encoded test waveform has no transients or hard frame joins.
		// Invalid per-frame synthesis/reservoir resets instead create
		// repeating clicks/chirps at or near 1,152-sample MPEG boundaries.
		maximumStep.Should().BeLessThan(0.14f);
		boundaryStep.Should().BeLessThan(0.14f);
	}

	[Test]
	public void PersistentDecoderYieldsSamePcmForDifferentReadBlockSizes()
	{
		byte[] encoded = Fixture();
		SamplePcmData imported = SampleAudioCodec.Decode(encoded, "tone.mp3");
		using MemoryStream source = new(encoded, writable: false);
		using MpegFile mpeg = new(source);
		float[] small = new float[137];
		long frame = 0;
		while (true)
		{
			int count = mpeg.ReadSamples(small, 0, small.Length);
			if (count == 0)
				break;
			for (int i = 0; i < count; i++)
			{
				frame.Should().BeLessThan(imported.FrameCount);
				small[i].Should().BeApproximately(
					imported.GetSample(frame, 0), 1e-6f);
				frame++;
			}
		}
		frame.Should().Be(imported.FrameCount);
	}

	[Test]
	public void ImportedLayer3RetainsOriginalBytesInModuleAndReloadsDecodedPcm()
	{
		byte[] encoded = Fixture();
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			id, "Layer III", "tone.mp3", encoded);
		document.Add(sample);
		sample.PcmData.Should().NotBeNull();
		sample.PendingAsset.Should().NotBeNull();
		sample.PendingAsset!.Bytes.ToArray().Should().Equal(encoded);

		string directory = Path.Combine(Path.GetTempPath(),
			"heresy-mp3-fixture-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string path = Path.Combine(directory, "layer3.hm");
			SongDocumentStorage.Save(path, document);
			sample.PendingAsset.Should().BeNull();
			using (ZipArchive archive = ZipFile.OpenRead(path))
			{
				ZipArchiveEntry entry = archive.Entries.Single(
					e => e.FullName == "pcm/tone.mp3");
				using Stream stream = entry.Open();
				using MemoryStream stored = new();
				stream.CopyTo(stored);
				stored.ToArray().Should().Equal(encoded,
					"save must not re-encode imported MPEG data");
			}
			SongDocument restored = SongDocumentStorage.Load(path);
			SampleDefinition loaded = (SampleDefinition)restored.Objects[id];
			loaded.PendingAsset.Should().BeNull();
			loaded.Asset.Should().NotBeNull();
			loaded.PcmData!.SampleRate.Should().Be(44100);
			loaded.PcmData.ChannelCount.Should().Be(1);
			loaded.PcmData.FrameCount.Should().Be(sample.PcmData!.FrameCount);
			loaded.PcmData.GetSample(2500, 0).Should().BeApproximately(
				sample.PcmData.GetSample(2500, 0), 1e-6f);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
