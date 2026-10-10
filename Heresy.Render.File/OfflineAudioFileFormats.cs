using System;

using Heresy.Render.Realtime;

namespace Heresy.Render.File;

public enum OfflineAudioFileFormat
{
	Flac,
	Mp3,
	Wave,
}

public static class OfflineAudioFileFormats
{
	public static OfflineAudioFileFormat DefaultLossless
		=> OfflineAudioFileFormat.Flac;


	/// <summary>
	/// Reject unsupported encoder formats before a temporary destination
	/// is created. PCM rendering stays format-neutral and unchanged.
	/// These constraints are specific to the actual codec/container.
	/// </summary>
	public static void ValidateOutput(
		OfflineAudioFileFormat fileFormat,
		AudioOutputFormat output,
		WavePcmBitDepth wavDepth = WavePcmBitDepth.Pcm16)
	{
		if (fileFormat != OfflineAudioFileFormat.Wave
			&& wavDepth != WavePcmBitDepth.Pcm16)
			throw new ArgumentException(
				"WAV bit depth may only be specified for WAV exports.",
				nameof(wavDepth));

		switch (fileFormat)
		{
			case OfflineAudioFileFormat.Wave:
				if (output.ChannelCount is not (1 or 2 or 6 or 8))
					throw new ArgumentException(
						"WAV export supports mono, stereo, 5.1 or 7.1.");
				if (wavDepth is not (WavePcmBitDepth.Pcm8
					or WavePcmBitDepth.Pcm16 or WavePcmBitDepth.Pcm24
					or WavePcmBitDepth.Pcm32))
					throw new ArgumentOutOfRangeException(nameof(wavDepth));
				break;
			case OfflineAudioFileFormat.Flac:
				if (output.ChannelCount is < 1 or > 8)
					throw new ArgumentException("FLAC supports up to 8 channels.");
				if (output.SampleRate > 655350)
					throw new ArgumentException(
						"FLAC sample rates must not exceed 655350 Hz.");
				break;
			case OfflineAudioFileFormat.Mp3:
				if (output.ChannelCount is not (1 or 2))
					throw new ArgumentException(
						"MP3 export supports only mono or stereo.");
				if (output.SampleRate is not (8000 or 11025 or 12000
					or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
					throw new ArgumentException(
						"MP3 requires a supported MPEG sample rate: 8, 11.025, " +
						"12, 16, 22.05, 24, 32, 44.1 or 48 kHz.");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(fileFormat));
		}
	}

	public static string GetDefaultExtension(
		OfflineAudioFileFormat format)
		=> format switch
		{
			OfflineAudioFileFormat.Flac => ".flac",
			OfflineAudioFileFormat.Mp3 => ".mp3",
			OfflineAudioFileFormat.Wave => ".wav",
			_ => throw new ArgumentOutOfRangeException(
				nameof(format)),
		};
}
