using System;
using System.Collections.Generic;
using System.Numerics;

namespace Heresy.Render.Configuration;

/// <summary>
/// Render settings which must be fixed for a deterministic render.
/// </summary>
public sealed class RenderConfiguration
{
	private readonly OutputChannelConfiguration[] _outputChannels;

	public RenderConfiguration(
		int sampleRate,
		IEnumerable<OutputChannelConfiguration> outputChannels)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		ArgumentNullException.ThrowIfNull(outputChannels);

		List<OutputChannelConfiguration> channels = [];
		foreach (OutputChannelConfiguration channel in outputChannels)
		{
			if (channel is null)
				throw new ArgumentException("Output channels may not contain null.", nameof(outputChannels));
			channels.Add(channel);
		}

		if (channels.Count == 0)
			throw new ArgumentException("At least one output channel is required.", nameof(outputChannels));

		SampleRate = sampleRate;
		_outputChannels = channels.ToArray();
	}

	public int SampleRate { get; }

	public IReadOnlyList<OutputChannelConfiguration> OutputChannels => _outputChannels;

	public int OutputChannelCount => _outputChannels.Length;

	public static RenderConfiguration Stereo(int sampleRate)
		=> new(
			sampleRate,
			new[]
			{
				new OutputChannelConfiguration(new Vector3(-1.0f, 0.0f, 0.0f)),
				new OutputChannelConfiguration(new Vector3(+1.0f, 0.0f, 0.0f)),
			});
}
