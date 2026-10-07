using System;

using Heresy.Core.Samples;

namespace Heresy.UserInterface.SampleEditing;

public sealed record SampleLoopAssistantResult(
	SampleLoop OriginalLoop,
	SampleLoop SuggestedLoop,
	double OriginalScore,
	double SuggestedScore,
	long SearchRadiusFrames)
{
	public bool Changed =>
		SuggestedLoop != OriginalLoop;

	public bool Improved =>
		SuggestedScore < OriginalScore;
}

public static class SampleLoopAssistant
{
	private const double SlopeWeight = 0.25;
	private const int MaximumCoordinatePasses = 4;

	public static SampleLoopAssistantResult Find(
		SamplePcmData pcm,
		SampleLoop loop,
		long searchRadiusFrames)
	{
		ArgumentNullException.ThrowIfNull(pcm);
		ArgumentNullException.ThrowIfNull(loop);
		if (searchRadiusFrames < 0)
			throw new ArgumentOutOfRangeException(nameof(searchRadiusFrames));
		if (loop.Mode == SampleLoopMode.None)
		{
			throw new InvalidOperationException(
				"The loop assistant requires an active loop.");
		}
		if (pcm.FrameCount == 0)
		{
			throw new InvalidOperationException(
				"The loop assistant requires decoded PCM frames.");
		}

		SampleLoop original =
			NormalizeLoop(
				loop,
				pcm.FrameCount);
		double originalScore =
			Score(
				pcm,
				original);

		long startMinimum =
			Math.Max(
				0,
				original.StartFrame
					- searchRadiusFrames);
		long startMaximum =
			Math.Min(
				pcm.FrameCount - 1,
				SaturatingAdd(
					original.StartFrame,
					searchRadiusFrames));
		long endMinimum =
			Math.Max(
				1,
				original.EndFrameExclusive
					- searchRadiusFrames);
		long endMaximum =
			Math.Min(
				pcm.FrameCount,
				SaturatingAdd(
					original.EndFrameExclusive,
					searchRadiusFrames));

		SampleLoop startFirst =
			Optimize(
				pcm,
				original,
				original,
				startMinimum,
				startMaximum,
				endMinimum,
				endMaximum,
				startFirst: true);
		SampleLoop endFirst =
			Optimize(
				pcm,
				original,
				original,
				startMinimum,
				startMaximum,
				endMinimum,
				endMaximum,
				startFirst: false);

		SampleLoop suggested =
			ChooseBetter(
				pcm,
				original,
				startFirst,
				endFirst);
		double suggestedScore =
			Score(
				pcm,
				suggested);

		return new SampleLoopAssistantResult(
			original,
			suggested,
			originalScore,
			suggestedScore,
			searchRadiusFrames);
	}

	private static SampleLoop Optimize(
		SamplePcmData pcm,
		SampleLoop original,
		SampleLoop initial,
		long startMinimum,
		long startMaximum,
		long endMinimum,
		long endMaximum,
		bool startFirst)
	{
		SampleLoop current = initial;

		for (int pass = 0;
			pass < MaximumCoordinatePasses;
			pass++)
		{
			SampleLoop before = current;

			if (startFirst)
			{
				current =
					FindBestStart(
						pcm,
						original,
						current,
						startMinimum,
						startMaximum);
				current =
					FindBestEnd(
						pcm,
						original,
						current,
						endMinimum,
						endMaximum);
			}
			else
			{
				current =
					FindBestEnd(
						pcm,
						original,
						current,
						endMinimum,
						endMaximum);
				current =
					FindBestStart(
						pcm,
						original,
						current,
						startMinimum,
						startMaximum);
			}

			if (current == before)
				break;
		}

		return current;
	}

	private static SampleLoop FindBestStart(
		SamplePcmData pcm,
		SampleLoop original,
		SampleLoop current,
		long minimum,
		long maximum)
	{
		SampleLoop best = current;
		double bestScore =
			Score(
				pcm,
				best);
		long bestMovement =
			Math.Abs(
				best.StartFrame
					- original.StartFrame);

		long allowedMaximum =
			Math.Min(
				maximum,
				current.EndFrameExclusive - 1);
		for (long start = minimum;
			start <= allowedMaximum;
			start++)
		{
			SampleLoop candidate =
				new(
					current.Mode,
					start,
					current.EndFrameExclusive);
			double candidateScore =
				Score(
					pcm,
					candidate);
			long movement =
				Math.Abs(
					start
						- original.StartFrame);

			if (IsBetter(
				candidateScore,
				movement,
				bestScore,
				bestMovement))
			{
				best = candidate;
				bestScore = candidateScore;
				bestMovement = movement;
			}
		}

		return best;
	}

	private static SampleLoop FindBestEnd(
		SamplePcmData pcm,
		SampleLoop original,
		SampleLoop current,
		long minimum,
		long maximum)
	{
		SampleLoop best = current;
		double bestScore =
			Score(
				pcm,
				best);
		long bestMovement =
			Math.Abs(
				best.EndFrameExclusive
					- original.EndFrameExclusive);

		long allowedMinimum =
			Math.Max(
				minimum,
				current.StartFrame + 1);
		for (long end = allowedMinimum;
			end <= maximum;
			end++)
		{
			SampleLoop candidate =
				new(
					current.Mode,
					current.StartFrame,
					end);
			double candidateScore =
				Score(
					pcm,
					candidate);
			long movement =
				Math.Abs(
					end
						- original.EndFrameExclusive);

			if (IsBetter(
				candidateScore,
				movement,
				bestScore,
				bestMovement))
			{
				best = candidate;
				bestScore = candidateScore;
				bestMovement = movement;
			}
		}

		return best;
	}

	private static SampleLoop ChooseBetter(
		SamplePcmData pcm,
		SampleLoop original,
		SampleLoop first,
		SampleLoop second)
	{
		double firstScore =
			Score(
				pcm,
				first);
		double secondScore =
			Score(
				pcm,
				second);

		long firstMovement =
			TotalMovement(
				original,
				first);
		long secondMovement =
			TotalMovement(
				original,
				second);

		if (IsBetter(
			secondScore,
				secondMovement,
				firstScore,
				firstMovement))
		{
			return second;
		}

		return first;
	}

	private static bool IsBetter(
		double candidateScore,
		long candidateMovement,
		double bestScore,
		long bestMovement)
	{
		if (candidateScore < bestScore)
			return true;
		if (candidateScore > bestScore)
			return false;

		return candidateMovement
			< bestMovement;
	}

	private static long TotalMovement(
		SampleLoop original,
		SampleLoop candidate)
		=> checked(
			Math.Abs(
				candidate.StartFrame
					- original.StartFrame)
			+ Math.Abs(
				candidate.EndFrameExclusive
					- original.EndFrameExclusive));

	private static double Score(
		SamplePcmData pcm,
		SampleLoop loop)
		=> loop.Mode switch
		{
			SampleLoopMode.Forward =>
				ForwardScore(
					pcm,
					loop),
			SampleLoopMode.PingPong =>
				PingPongScore(
					pcm,
					loop),
			_ =>
				double.PositiveInfinity,
		};

	private static double ForwardScore(
		SamplePcmData pcm,
		SampleLoop loop)
	{
		double score = 0.0;
		long loopLength =
			loop.EndFrameExclusive
				- loop.StartFrame;

		for (int channel = 0;
			channel < pcm.ChannelCount;
			channel++)
		{
			float start =
				pcm.GetSample(
					loop.StartFrame,
					channel);
			float end =
				pcm.GetSample(
					loop.EndFrameExclusive - 1,
					channel);
			if (!float.IsFinite(start)
				|| !float.IsFinite(end))
			{
				return double.PositiveInfinity;
			}

			double valueDelta =
				start - end;
			double startSlope = 0.0;
			double endSlope = 0.0;

			if (loopLength > 1)
			{
				float startNext =
					pcm.GetSample(
						loop.StartFrame + 1,
						channel);
				float endPrevious =
					pcm.GetSample(
						loop.EndFrameExclusive - 2,
						channel);
				if (!float.IsFinite(startNext)
					|| !float.IsFinite(endPrevious))
				{
					return double.PositiveInfinity;
				}

				startSlope =
					startNext - start;
				endSlope =
					end - endPrevious;
			}

			double slopeDelta =
				startSlope - endSlope;
			score +=
				(valueDelta * valueDelta)
				+ (SlopeWeight
					* slopeDelta
					* slopeDelta);
		}

		return score
			/ pcm.ChannelCount;
	}

	private static double PingPongScore(
		SamplePcmData pcm,
		SampleLoop loop)
	{
		double score = 0.0;
		long loopLength =
			loop.EndFrameExclusive
				- loop.StartFrame;
		if (loopLength <= 1)
			return 0.0;

		for (int channel = 0;
			channel < pcm.ChannelCount;
			channel++)
		{
			float start =
				pcm.GetSample(
					loop.StartFrame,
					channel);
			float startNext =
				pcm.GetSample(
					loop.StartFrame + 1,
					channel);
			float end =
				pcm.GetSample(
					loop.EndFrameExclusive - 1,
					channel);
			float endPrevious =
				pcm.GetSample(
					loop.EndFrameExclusive - 2,
					channel);
			if (!float.IsFinite(start)
				|| !float.IsFinite(startNext)
				|| !float.IsFinite(end)
				|| !float.IsFinite(endPrevious))
			{
				return double.PositiveInfinity;
			}

			double startSlope =
				startNext - start;
			double endSlope =
				end - endPrevious;
			score +=
				(startSlope * startSlope)
				+ (endSlope * endSlope);
		}

		return score
			/ pcm.ChannelCount;
	}

	private static SampleLoop NormalizeLoop(
		SampleLoop loop,
		long frameCount)
	{
		long start =
			Math.Clamp(
				loop.StartFrame,
				0,
				frameCount - 1);
		long end =
			Math.Clamp(
				loop.EndFrameExclusive,
				start + 1,
				frameCount);

		return new SampleLoop(
			loop.Mode,
			start,
			end);
	}

	private static long SaturatingAdd(
		long value,
		long amount)
	{
		if (amount > long.MaxValue - value)
			return long.MaxValue;
		return value + amount;
	}
}
