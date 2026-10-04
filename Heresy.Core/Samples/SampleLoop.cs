using System;

namespace Heresy.Core.Samples;

public enum SampleLoopMode
{
	None = 0,
	Forward,
	PingPong,
}

public sealed record SampleLoop
{
	public SampleLoop(SampleLoopMode mode, long startFrame, long endFrameExclusive)
	{
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (endFrameExclusive < startFrame)
			throw new ArgumentOutOfRangeException(nameof(endFrameExclusive));
		if (mode != SampleLoopMode.None && endFrameExclusive == startFrame)
			throw new ArgumentException("A looping region must contain at least one frame.");

		Mode = mode;
		StartFrame = startFrame;
		EndFrameExclusive = endFrameExclusive;
	}

	public SampleLoopMode Mode { get; }

	public long StartFrame { get; }

	public long EndFrameExclusive { get; }

	public void Deconstruct(
		out SampleLoopMode mode,
		out long startFrame,
		out long endFrameExclusive)
	{
		mode = Mode;
		startFrame = StartFrame;
		endFrameExclusive = EndFrameExclusive;
	}
}
