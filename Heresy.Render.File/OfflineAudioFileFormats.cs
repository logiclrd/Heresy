using System;

namespace Heresy.Render.File;

public enum OfflineAudioFileFormat
{
	Flac,
	Wave,
}

public static class OfflineAudioFileFormats
{
	public static OfflineAudioFileFormat DefaultLossless
		=> OfflineAudioFileFormat.Flac;

	public static string GetDefaultExtension(
		OfflineAudioFileFormat format)
		=> format switch
		{
			OfflineAudioFileFormat.Flac => ".flac",
			OfflineAudioFileFormat.Wave => ".wav",
			_ => throw new ArgumentOutOfRangeException(
				nameof(format)),
		};
}
