using System;

using Heresy.Core.Samples;
using Heresy.Render.Samples;

namespace Heresy.Playback;

/// <summary>
/// Realtime-safe sample-data adapter. It only exposes PCM already owned by the
/// live/snapshotted sample definition and never performs file or codec work.
/// </summary>
public sealed class InMemorySampleDataProvider
	: ISampleDataProvider
{
	public ISampleData GetSampleData(
		SampleDefinition sample)
	{
		ArgumentNullException.ThrowIfNull(sample);

		SamplePcmData pcm =
			sample.PcmData
				?? throw new InvalidOperationException(
					$"Sample '{sample.Name}' ({sample.Id}) has no decoded PCM. Samples must be hydrated during load/import before playback.");

		return new Adapter(pcm);
	}

	private sealed class Adapter
		: ISampleData
	{
		private readonly SamplePcmData _pcm;

		public Adapter(SamplePcmData pcm)
			=> _pcm = pcm;

		public int SampleRate => _pcm.SampleRate;
		public int ChannelCount => _pcm.ChannelCount;
		public long FrameCount => _pcm.FrameCount;

		public float GetSample(long frame, int channel)
			=> _pcm.GetSample(frame, channel);
	}
}
