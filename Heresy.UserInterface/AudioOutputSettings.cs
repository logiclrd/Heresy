using System;
using System.Numerics;
using System.Threading;

using Heresy.Render.Configuration;

namespace Heresy.UserInterface;

/// <summary>
/// Session-level (not song-file) output preferences. Every new realtime or
/// offline request captures one immutable RenderConfiguration. Active PCM
/// sessions are never reconfigured or resampled on the audio callback.
/// </summary>
public sealed class AudioOutputSettings
{
	private RenderConfiguration _current =
		RenderConfiguration.Stereo(sampleRate: 48000);

	public RenderConfiguration Current => Volatile.Read(ref _current);

	public void Set(RenderConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		Volatile.Write(ref _current, configuration);
	}

	/// <summary>Preset output ordering: mono; FL/FR; or FL,FR,C,LFE,
	/// rear-left,rear-right[,side-left,side-right]. LFE is a speaker
	/// output, not an automatic bass-management crossover.</summary>
	public static RenderConfiguration Preset(
		int sampleRate, int channels)
	{
		Vector3[] positions = channels switch
		{
			1 => [Vector3.Zero],
			2 => [new(-1, 0, 0), new(1, 0, 0)],
			6 =>
			[
				new(-1, 0, 1), new(1, 0, 1),
				new(0, 0, 1), new(0, 0, 0),
				new(-1, 0, -1), new(1, 0, -1),
			],
			8 =>
			[
				new(-1, 0, 1), new(1, 0, 1),
				new(0, 0, 1), new(0, 0, 0),
				new(-1, 0, -1), new(1, 0, -1),
				new(-1, 0, 0), new(1, 0, 0),
			],
			_ => throw new ArgumentOutOfRangeException(
				nameof(channels), "Supported presets: mono, stereo, 5.1 or 7.1."),
		};
		OutputChannelConfiguration[] outputs = new OutputChannelConfiguration[
			positions.Length];
		for (int i = 0; i < positions.Length; i++)
			outputs[i] = new OutputChannelConfiguration(positions[i]);
		return new RenderConfiguration(sampleRate, outputs);
	}

	public static string SpeakerName(int channels, int index)
	{
		if (index < 0 || index >= channels)
			throw new ArgumentOutOfRangeException(nameof(index));
		string[] names = channels switch
		{
			1 => ["Mono"],
			2 => ["Front L", "Front R"],
			6 => ["Front L", "Front R", "Center", "LFE",
				"Rear L", "Rear R"],
			8 => ["Front L", "Front R", "Center", "LFE",
				"Rear L", "Rear R", "Side L", "Side R"],
			_ => [],
		};
		return names.Length == 0 ? $"Output {index + 1}" : names[index];
	}
}
