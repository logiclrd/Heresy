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
