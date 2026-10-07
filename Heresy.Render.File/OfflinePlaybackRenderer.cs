using System;

using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Render.File;

public sealed class IndefiniteOfflineRenderException
	: InvalidOperationException
{
	public IndefiniteOfflineRenderException()
		: base(
			"Offline rendering cannot finish because at least one voice has no deterministic end after end-of-input Note Off.")
	{
	}
}

public readonly record struct OfflineRenderResult(
	long LogicalFrameCount,
	long TailFrameCount)
{
	public long TotalFrameCount =>
		checked(
			LogicalFrameCount
				+ TailFrameCount);
}

/// <summary>
/// Renders a sequential PlaybackSession through its logical arrangement end,
/// releases every still-active voice, then drains deterministic release and
/// anti-click tails. A genuinely unbounded post-release voice is rejected
/// rather than being truncated at an arbitrary timeout.
/// </summary>
public static class OfflinePlaybackRenderer
{
	public const int DefaultBlockFrameCount = 4096;

	public static OfflineRenderResult Render(
		PlaybackSession session,
		TimeSpan logicalDuration,
		IAudioFileSink sink,
		int blockFrameCount = DefaultBlockFrameCount)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(sink);
		if (logicalDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(logicalDuration));
		if (blockFrameCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(blockFrameCount));

		AudioOutputFormat expected =
			new(
				session.SampleRate,
				session.OutputChannelCount);
		if (sink.Format != expected)
		{
			throw new ArgumentException(
				"Audio file sink format must match the PlaybackSession render format.",
				nameof(sink));
		}

		long logicalFrames =
			FrameTime.Ceiling(
				logicalDuration,
				session.SampleRate);
		if (session.NextFrame > logicalFrames)
		{
			throw new InvalidOperationException(
				"The playback session has already advanced beyond the requested logical render duration.");
		}

		RenderLogicalBody(
			session,
			sink,
			logicalFrames,
			blockFrameCount);

		session.EndInput();
		if (session.HasIndefiniteActiveVoices)
			throw new IndefiniteOfflineRenderException();

		long tailFrames =
			RenderTail(
				session,
				sink,
				blockFrameCount);

		return new OfflineRenderResult(
			logicalFrames,
			tailFrames);
	}

	private static void RenderLogicalBody(
		PlaybackSession session,
		IAudioFileSink sink,
		long logicalFrames,
		int blockFrameCount)
	{
		int channels =
			session.OutputChannelCount;
		float[] buffer =
			new float[
				checked(
					blockFrameCount
						* channels)];

		while (session.NextFrame < logicalFrames)
		{
			int frameCount =
				(int)Math.Min(
					blockFrameCount,
					logicalFrames
						- session.NextFrame);
			Span<float> block =
				buffer.AsSpan(
					0,
					checked(
						frameCount
							* channels));
			session.Render(
				session.NextFrame,
				frameCount,
				block);
			sink.Write(block);
		}
	}

	private static long RenderTail(
		PlaybackSession session,
		IAudioFileSink sink,
		int blockFrameCount)
	{
		if (session.IsQuiescent)
			return 0;

		int channels =
			session.OutputChannelCount;
		float[] buffer =
			new float[
				checked(
					blockFrameCount
						* channels)];
		long writtenFrames = 0;

		while (!session.IsQuiescent)
		{
			if (session.HasIndefiniteActiveVoices)
				throw new IndefiniteOfflineRenderException();

			Span<float> block =
				buffer.AsSpan();
			session.Render(
				session.NextFrame,
				blockFrameCount,
				block);

			int framesToWrite =
				session.IsQuiescent
					? FindLastNonZeroFrameExclusive(
						block,
						channels)
					: blockFrameCount;
			if (framesToWrite != 0)
			{
				sink.Write(
					block.Slice(
						0,
						checked(
							framesToWrite
								* channels)));
				writtenFrames =
					checked(
						writtenFrames
							+ framesToWrite);
			}
		}

		return writtenFrames;
	}

	private static int FindLastNonZeroFrameExclusive(
		ReadOnlySpan<float> samples,
		int channelCount)
	{
		int frameCount =
			samples.Length
				/ channelCount;
		for (int frame = frameCount - 1;
			frame >= 0;
			frame--)
		{
			int start =
				frame
					* channelCount;
			for (int channel = 0;
				channel < channelCount;
				channel++)
			{
				if (samples[start + channel] != 0.0f)
					return frame + 1;
			}
		}
		return 0;
	}
}
