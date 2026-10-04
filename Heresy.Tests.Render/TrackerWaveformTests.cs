using System;

using Heresy.Core.Sequencing;
using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerWaveformTests
{
	[Test]
	public void RampDownMatchesImpulseTrackerAnchorTable()
	{
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.RampDown,
				0),
			Is.EqualTo(64));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.RampDown,
				1),
			Is.EqualTo(63));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.RampDown,
				127),
			Is.EqualTo(0));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.RampDown,
				128),
			Is.EqualTo(0));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.RampDown,
				255),
			Is.EqualTo(-64));
	}

	[Test]
	public void SquareMatchesImpulseTrackerUnipolarTable()
	{
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.Square,
				0),
			Is.EqualTo(64));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.Square,
				127),
			Is.EqualTo(64));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.Square,
				128),
			Is.EqualTo(0));
		Assert.That(
			TrackerVibrato.GetWaveformSample(
				TrackerWaveform.Square,
				255),
			Is.EqualTo(0));
	}

	[Test]
	public void RampDownIsContinuousBetweenLegacyPhaseAnchors()
	{
		double at20 =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				TrackerWaveform.RampDown,
				20.0,
				5);
		double at21 =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				TrackerWaveform.RampDown,
				21.0,
				5);
		double between =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				TrackerWaveform.RampDown,
				20.5,
				5);

		Assert.That(
			between,
			Is.EqualTo((at20 + at21) / 2.0).Within(1e-14));
	}

	[Test]
	public void SquareKeepsHardWaveformEdgeAtHalfCycle()
	{
		double before =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				TrackerWaveform.Square,
				127.999,
				5);
		double after =
			TrackerVibrato.GetContinuousLinearSlideUnits(
				TrackerWaveform.Square,
				128.0,
				5);

		Assert.That(before, Is.GreaterThan(0.0));
		Assert.That(after, Is.EqualTo(0.0));
	}

	[Test]
	public void RandomAnchorGeneratorIsDeterministicAndVoiceSeeded()
	{
		int a0 = TrackerVibrato.GetRandomWaveformSample(
			0x12345678UL,
			0);
		int a0Again = TrackerVibrato.GetRandomWaveformSample(
			0x12345678UL,
			0);
		int a1 = TrackerVibrato.GetRandomWaveformSample(
			0x12345678UL,
			1);
		int b0 = TrackerVibrato.GetRandomWaveformSample(
			0x87654321UL,
			0);

		Assert.That(a0Again, Is.EqualTo(a0));
		Assert.That(a1, Is.Not.EqualTo(a0));
		Assert.That(b0, Is.Not.EqualTo(a0));

		Assert.That(a0, Is.InRange(-64, 63));
		Assert.That(a1, Is.InRange(-64, 63));
		Assert.That(b0, Is.InRange(-64, 63));
	}

	[Test]
	public void RandomVibratoInterpolatesBetweenTrackerTickAnchors()
	{
		TrackerVibratoPitchCurve curve = new(
			initialPhase: 20,
			speed: 5,
			depth: 4,
			tickDuration: TimeSpan.FromMilliseconds(20),
			sampleRate: 1000,
			waveform: TrackerWaveform.Random,
			randomSeed: 0x12345678UL,
			randomStartIndex: 7);

		double at0 = curve.GetMultiplier(0);
		double at20 = curve.GetMultiplier(20);
		double at10 = curve.GetMultiplier(10);

		double log0 = Math.Log2(at0);
		double log20 = Math.Log2(at20);
		double log10 = Math.Log2(at10);

		Assert.That(
			log10,
			Is.EqualTo((log0 + log20) / 2.0).Within(1e-14));
	}

	[Test]
	public void RandomTremoloInterpolatesBetweenTrackerTickAnchors()
	{
		TrackerTremoloVolumeCurve curve = new(
			initialPhase: 20,
			speed: 5,
			depth: 4,
			tickDuration: TimeSpan.FromMilliseconds(20),
			sampleRate: 1000,
			waveform: TrackerWaveform.Random,
			randomSeed: 0x12345678UL,
			randomStartIndex: 7);

		double at0 = curve.GetOffsetTrackerUnits(0);
		double at20 = curve.GetOffsetTrackerUnits(20);
		double at10 = curve.GetOffsetTrackerUnits(10);

		Assert.That(
			at10,
			Is.EqualTo((at0 + at20) / 2.0).Within(1e-14));
	}
}
