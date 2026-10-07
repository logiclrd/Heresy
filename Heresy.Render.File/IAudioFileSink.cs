using System;

using Heresy.Render.Realtime;

namespace Heresy.Render.File;

public interface IAudioFileSink : IDisposable
{
	AudioOutputFormat Format { get; }

	long FramesWritten { get; }

	void Write(
		ReadOnlySpan<float> interleavedSamples);

	void Complete();
}
