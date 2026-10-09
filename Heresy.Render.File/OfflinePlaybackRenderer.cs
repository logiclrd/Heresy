using System;

using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Render.File;

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
/// releases every still-active voice, preserves deterministic release tails,
/// then cuts only voices that remain unbounded after Note Off and drains their
/// ordinary anti-click residue.
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
		session.CutIndefiniteActiveVoicesAfterEndInput();

		long tailFrames =
			RenderTail(
				session,
				sink,
				blockFrameCount);

		return new OfflineRenderResult(
			logicalFrames,
			tailFrames);
	}

	/// <summary>
	/// Stream the arrangement from a coroutine generator directly into the
	/// file sink. No full-song schedule is constructed, and the logical end
	/// is discovered as the root generator naturally completes. Release
	/// tails retain the same PlaybackSession policy as the finite overload.
	/// </summary>
	public static OfflineRenderResult Render(
		IIncrementalArrangementSource arrangement,
		IAudioFileSink sink,
		int blockFrameCount = DefaultBlockFrameCount,
		long maximumLogicalFrames = 0,
		long maximumTailFrames = 0)
	{
		ArgumentNullException.ThrowIfNull(arrangement);
		ArgumentNullException.ThrowIfNull(sink);
		if (blockFrameCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(blockFrameCount));
		if (maximumLogicalFrames < 0)
			throw new ArgumentOutOfRangeException(nameof(maximumLogicalFrames));
		if (maximumLogicalFrames == 0)
			maximumLogicalFrames = checked(
				(long)arrangement.Format.SampleRate * 60 * 60 * 4);
		if (maximumTailFrames < 0)
			throw new ArgumentOutOfRangeException(nameof(maximumTailFrames));
		if (maximumTailFrames == 0)
			maximumTailFrames = checked(
				(long)arrangement.Format.SampleRate * 60 * 5);
		if (sink.Format != arrangement.Format)
			throw new ArgumentException(
				"Audio file sink format must match the incremental source.", nameof(sink));
		float[] samples = new float[checked(blockFrameCount * arrangement.Format.ChannelCount)];
		long frames = 0;
		while (true)
		{
			int wanted = (int)Math.Min(blockFrameCount,
				maximumLogicalFrames - frames);
			if (wanted == 0)
				throw new InvalidOperationException(
					"Offline arrangement exceeded the finite export frame limit.");
			Span<float> block = samples.AsSpan(0, wanted * arrangement.Format.ChannelCount);
			block.Clear();
			int produced = arrangement.RenderLogical(wanted, block);
			if (produced < 0 || produced > wanted)
				throw new InvalidOperationException(
					"Incremental arrangement returned an invalid frame count.");
			if (produced != 0)
			{
				sink.Write(block.Slice(0, produced * arrangement.Format.ChannelCount));
				frames = checked(frames + produced);
			}
			if (produced < wanted)
			{
				if (!arrangement.IsComplete)
					throw new InvalidOperationException(
						"Incremental arrangement stopped before its natural end.");
				break;
			}
			if (arrangement.IsComplete
				&& frames >= FrameTime.Ceiling(arrangement.LogicalDuration,
					arrangement.Format.SampleRate))
				break;
		}

		PlaybackSession session = arrangement.Session;
		session.EndInput();
		session.CutIndefiniteActiveVoicesAfterEndInput();
		long tails = RenderTail(session, sink, blockFrameCount,
			maximumTailFrames);
		return new OfflineRenderResult(frames, tails);
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
		int blockFrameCount,
		long maximumTailFrames = long.MaxValue)
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
				session.CutIndefiniteActiveVoicesAfterEndInput();

			long budget = maximumTailFrames - writtenFrames;
			if (budget <= 0)
				throw new InvalidOperationException(
					"Offline arrangement's recursive release tail exceeded the export frame limit.");
			int frames = (int)Math.Min(blockFrameCount, budget);
			Span<float> block = buffer.AsSpan(0, frames * channels);
			session.Render(session.NextFrame, frames, block);

			int framesToWrite = session.IsQuiescent
				? FindLastNonZeroFrameExclusive(block, channels)
				: frames;
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
