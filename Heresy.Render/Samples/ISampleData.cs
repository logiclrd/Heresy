namespace Heresy.Render.Samples;

/// <summary>
/// Decoded source PCM. Implementations may be memory-backed, mapped, streamed,
/// or otherwise lazy; render code addresses it by absolute source frame.
/// </summary>
public interface ISampleData
{
	int SampleRate { get; }
	int ChannelCount { get; }
	long FrameCount { get; }

	float GetSample(long frame, int channel);
}
/// <summary>A decoded sample-data provider may opt in to identity
/// comparison when it exposes the same immutable PCM object across frozen
/// song snapshots. Arbitrary sample providers must not be presumed
/// equivalent merely because their dimensions or object IDs match.</summary>
public interface IImmutableSamplePcmIdentity
{
	Heresy.Core.Samples.SamplePcmData ImmutablePcm { get; }
}
