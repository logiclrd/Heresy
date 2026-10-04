using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerArpeggioPitchCurveTests
{
	[Test]
	public void ArpeggioRemainsDiscreteAtTrackerTickBoundaries()
	{
		TrackerArpeggioPitchCurve curve = new(
			firstSemitones: 4,
			secondSemitones: 7,
			tickDuration: TimeSpan.FromMilliseconds(20),
			sampleRate: 1000);

		Assert.That(curve.GetMultiplier(0), Is.EqualTo(1.0));
		Assert.That(curve.GetMultiplier(19), Is.EqualTo(1.0));

		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(Math.Pow(2.0, 4.0 / 12.0)).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(39),
			Is.EqualTo(Math.Pow(2.0, 4.0 / 12.0)).Within(1e-14));

		Assert.That(
			curve.GetMultiplier(40),
			Is.EqualTo(Math.Pow(2.0, 7.0 / 12.0)).Within(1e-14));
		Assert.That(curve.GetMultiplier(60), Is.EqualTo(1.0));
	}

	[Test]
	public void ArpeggioRepeatsEveryThreeTicks()
	{
		TrackerArpeggioPitchCurve curve = new(
			3,
			7,
			TimeSpan.FromMilliseconds(20),
			1000);

		Assert.That(curve.GetMultiplier(80), Is.EqualTo(curve.GetMultiplier(20)));
		Assert.That(curve.GetMultiplier(100), Is.EqualTo(curve.GetMultiplier(40)));
	}
}
