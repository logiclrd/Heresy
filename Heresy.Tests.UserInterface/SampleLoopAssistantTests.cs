using System;

using AwesomeAssertions;

using Heresy.Core.Samples;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleLoopAssistantTests
{
	[Test]
	public void ForwardLoopFindsMatchingValueAndSlopeNearBothHandles()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				0.2f,
				0.6f,
				1.0f,
				0.4f,
				-0.2f,
				0.15f,
				0.55f,
				0.95f,
				0.35f,
				-0.25f);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				1,
				9);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 2);

		result.SuggestedLoop.Should().Be(
			new SampleLoop(
				SampleLoopMode.Forward,
				2,
				8));
		result.SuggestedScore.Should()
			.BeLessThan(result.OriginalScore);
	}

	[Test]
	public void ForwardLoopUsesEverySourceChannel()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 48000,
				channelCount: 2,
				interleavedSamples:
				[
					0.0f, 0.0f,
					0.5f, 0.9f,
					1.0f, 0.0f,
					0.5f, -0.9f,
					0.0f, 0.0f,
					0.5f, 0.9f,
					1.0f, 0.0f,
					0.5f, -0.9f,
					0.0f, 0.0f,
				]);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				1,
				7);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 2);

		result.SuggestedLoop.Should().Be(
			new SampleLoop(
				SampleLoopMode.Forward,
				0,
				5));
	}

	[Test]
	public void PingPongLoopFindsFlatTurningPoints()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				0.5f,
				1.0f,
				1.0f,
				0.5f,
				0.0f,
				-0.5f,
				-1.0f,
				-1.0f,
				-0.5f,
				0.0f);
		SampleLoop loop =
			new(
				SampleLoopMode.PingPong,
				1,
				10);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 3);

		result.SuggestedLoop.Should().Be(
			new SampleLoop(
				SampleLoopMode.PingPong,
				2,
				9));
		result.SuggestedScore.Should()
			.BeLessThan(result.OriginalScore);
	}

	[Test]
	public void EqualScoresPreferSmallestMovementFromCurrentLoop()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				0.0f,
				0.0f,
				0.0f,
				0.0f,
				0.0f,
				0.0f,
				0.0f);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				2,
				6);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 3);

		result.SuggestedLoop.Should().Be(loop);
		result.Improved.Should().BeFalse();
	}

	[Test]
	public void SearchWindowClampsToDecodedPcmAndKeepsLoopNonEmpty()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				0.1f,
				0.2f);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				0,
				3);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 100);

		result.SuggestedLoop.StartFrame.Should()
			.BeInRange(0, 2);
		result.SuggestedLoop.EndFrameExclusive.Should()
			.BeInRange(
				result.SuggestedLoop.StartFrame + 1,
				3);
	}

	[Test]
	public void ExistingOutOfRangeLoopIsNormalizedBeforeSearch()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				0.5f,
				0.0f,
				-0.5f);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				2,
				20);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 1);

		result.OriginalLoop.Should().Be(
			new SampleLoop(
				SampleLoopMode.Forward,
				2,
				4));
		result.SuggestedLoop.EndFrameExclusive.Should()
			.BeLessThanOrEqualTo(4);
	}

	[Test]
	public void InactiveLoopIsRejected()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				1.0f);

		Action action =
			() => SampleLoopAssistant.Find(
				pcm,
				new SampleLoop(
					SampleLoopMode.None,
					0,
					0),
				searchRadiusFrames: 10);

		action.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("*active loop*");
	}

	[Test]
	public void EmptyPcmIsRejected()
	{
		SamplePcmData pcm =
			Mono();

		Action action =
			() => SampleLoopAssistant.Find(
				pcm,
				new SampleLoop(
					SampleLoopMode.Forward,
					0,
					1),
				searchRadiusFrames: 10);

		action.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("*decoded PCM*");
	}

	[Test]
	public void NonFiniteCandidateSamplesAreNotChosen()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				float.NaN,
				0.5f,
				0.0f,
				0.5f,
				0.0f);
		SampleLoop loop =
			new(
				SampleLoopMode.Forward,
				2,
				6);

		SampleLoopAssistantResult result =
			SampleLoopAssistant.Find(
				pcm,
				loop,
				searchRadiusFrames: 2);

		double.IsFinite(result.SuggestedScore)
			.Should().BeTrue();
	}

	[Test]
	public void NegativeSearchRadiusIsRejected()
	{
		SamplePcmData pcm =
			Mono(
				0.0f,
				1.0f);

		Action action =
			() => SampleLoopAssistant.Find(
				pcm,
				new SampleLoop(
					SampleLoopMode.Forward,
					0,
					2),
				searchRadiusFrames: -1);

		action.Should()
			.Throw<ArgumentOutOfRangeException>();
	}

	private static SamplePcmData Mono(
		params float[] samples)
		=> new(
			sampleRate: 48000,
			channelCount: 1,
			interleavedSamples: samples);
}
