using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerPitchSlideCurveTests
{
	[Test]
	public void SlideMatchesTrackerTickAnchorsAndHoldsFinalTick()
	{
		TrackerPitchSlideCurve curve = new(
			initialMultiplier: 1.0,
			linearUnitsPerTick: 48.0,
			tickDuration: TimeSpan.FromMilliseconds(20),
			ticksPerRow: 6,
			sampleRate: 1000);

		Assert.That(curve.GetMultiplier(0), Is.EqualTo(1.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(Math.Pow(2.0, 48.0 / 768.0)).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(100),
			Is.EqualTo(Math.Pow(2.0, 240.0 / 768.0)).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(120),
			Is.EqualTo(curve.GetMultiplier(100)).Within(1e-14));
	}

	[Test]
	public void SlideIsContinuousBetweenTickAnchors()
	{
		TrackerPitchSlideCurve curve = new(
			1.0,
			48.0,
			TimeSpan.FromMilliseconds(20),
			6,
			1000);

		Assert.That(
			curve.GetMultiplier(10),
			Is.EqualTo(Math.Pow(2.0, 24.0 / 768.0)).Within(1e-14));
	}
}
