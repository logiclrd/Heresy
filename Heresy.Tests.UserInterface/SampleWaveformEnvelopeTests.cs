using AwesomeAssertions;

using Heresy.Core.Samples;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleWaveformEnvelopeTests
{
	[Test]
	public void BuildCapturesIndependentMinMaxPeaksPerChannel()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 48000,
				channelCount: 2,
				interleavedSamples:
				[
					-1.0f, 0.25f,
					0.5f, -0.75f,
					0.75f, 0.5f,
					-0.25f, 1.0f,
				]);

		SampleWaveformEnvelope envelope =
			SampleWaveformEnvelope.Build(
				pcm,
				columnCount: 2);

		envelope.FrameCount.Should().Be(4);
		envelope.ChannelCount.Should().Be(2);
		envelope.ColumnCount.Should().Be(2);

		envelope[0, 0].Minimum.Should().Be(-1.0f);
		envelope[0, 0].Maximum.Should().Be(0.5f);
		envelope[0, 1].Minimum.Should().Be(-0.75f);
		envelope[0, 1].Maximum.Should().Be(0.25f);

		envelope[1, 0].Minimum.Should().Be(-0.25f);
		envelope[1, 0].Maximum.Should().Be(0.75f);
		envelope[1, 1].Minimum.Should().Be(0.5f);
		envelope[1, 1].Maximum.Should().Be(1.0f);
	}

	[Test]
	public void MoreColumnsThanFramesRepeatNearestFrameWithoutEmptyGaps()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 44100,
				channelCount: 1,
				interleavedSamples:
				[
					-0.5f,
					0.75f,
				]);

		SampleWaveformEnvelope envelope =
			SampleWaveformEnvelope.Build(
				pcm,
				columnCount: 5);

		for (int column = 0; column < envelope.ColumnCount; column++)
		{
			SampleWaveformPeak peak = envelope[column, 0];
			peak.HasData.Should().BeTrue();
		}

		envelope[0, 0].Minimum.Should().Be(-0.5f);
		envelope[4, 0].Maximum.Should().Be(0.75f);
	}

	[Test]
	public void NonFiniteSamplesAreIgnoredForDisplayPeaks()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 48000,
				channelCount: 1,
				interleavedSamples:
				[
					float.NaN,
					float.PositiveInfinity,
					-0.25f,
					0.5f,
				]);

		SampleWaveformEnvelope envelope =
			SampleWaveformEnvelope.Build(
				pcm,
				columnCount: 1);

		envelope[0, 0].HasData.Should().BeTrue();
		envelope[0, 0].Minimum.Should().Be(-0.25f);
		envelope[0, 0].Maximum.Should().Be(0.5f);
	}

	[Test]
	public void AllNonFiniteBinProducesNoDataPeak()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 48000,
				channelCount: 1,
				interleavedSamples:
				[
					float.NaN,
					float.NegativeInfinity,
				]);

		SampleWaveformEnvelope envelope =
			SampleWaveformEnvelope.Build(
				pcm,
				columnCount: 1);

		envelope[0, 0].HasData.Should().BeFalse();
	}

	[Test]
	public void EmptyPcmProducesZeroLengthEnvelope()
	{
		SamplePcmData pcm =
			new(
				sampleRate: 48000,
				channelCount: 2,
				interleavedSamples: []);

		SampleWaveformEnvelope envelope =
			SampleWaveformEnvelope.Build(
				pcm,
				columnCount: 10);

		envelope.FrameCount.Should().Be(0);
		envelope.ColumnCount.Should().Be(10);
		envelope[0, 0].HasData.Should().BeFalse();
	}

	[TestCase(0L, 0.0)]
	[TestCase(25L, 0.25)]
	[TestCase(50L, 0.5)]
	[TestCase(100L, 1.0)]
	public void FrameFractionMapsWholeSampleRange(
		long frame,
		double expected)
	{
		SampleWaveformEnvelope.FrameToFraction(
				frame,
				frameCount: 100)
			.Should().BeApproximately(expected, 1e-12);
	}

	[TestCase(0.0, 0L)]
	[TestCase(0.25, 25L)]
	[TestCase(0.5, 50L)]
	[TestCase(1.0, 100L)]
	public void FractionMapsBackToFrameBoundary(
		double fraction,
		long expected)
	{
		SampleWaveformEnvelope.FractionToFrame(
				fraction,
				frameCount: 100)
			.Should().Be(expected);
	}
}
