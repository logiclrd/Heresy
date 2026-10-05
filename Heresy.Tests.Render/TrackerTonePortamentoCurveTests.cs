using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerTonePortamentoCurveTests
{
	[Test]
	public void UpwardPortamentoSpreadsLegacyRowTotalAcrossWholeRow()
	{
		TrackerTonePortamentoCurve curve = new(
			initialMultiplier: 1.0,
			targetMultiplier: 2.0,
			linearUnitsPerTick: 48.0,
			tickDuration: TimeSpan.FromMilliseconds(20),
			ticksPerRow: 6,
			sampleRate: 1000);

		Assert.That(curve.GetMultiplier(0), Is.EqualTo(1.0));
		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(Math.Pow(2.0, 40.0 / 768.0)).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(100),
			Is.EqualTo(Math.Pow(2.0, 200.0 / 768.0)).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(120),
			Is.EqualTo(Math.Pow(2.0, 240.0 / 768.0)).Within(1e-14));
	}

	[Test]
	public void DownwardPortamentoMovesInOppositeDirection()
	{
		TrackerTonePortamentoCurve curve = new(
			initialMultiplier: 2.0,
			targetMultiplier: 1.0,
			linearUnitsPerTick: 48.0,
			tickDuration: TimeSpan.FromMilliseconds(20),
			ticksPerRow: 6,
			sampleRate: 1000);

		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(2.0 * Math.Pow(2.0, -40.0 / 768.0)).Within(1e-14));
	}

	[Test]
	public void PortamentoClampsAtTargetWithoutOvershoot()
	{
		TrackerTonePortamentoCurve curve = new(
			initialMultiplier: 1.0,
			targetMultiplier: 1.05,
			linearUnitsPerTick: 192.0,
			tickDuration: TimeSpan.FromMilliseconds(20),
			ticksPerRow: 6,
			sampleRate: 1000);

		Assert.That(curve.GetMultiplier(10), Is.EqualTo(1.05).Within(1e-14));
		Assert.That(curve.GetMultiplier(20), Is.EqualTo(1.05).Within(1e-14));
	}

	[Test]
	public void PortamentoIsContinuousAcrossWholeRow()
	{
		TrackerTonePortamentoCurve curve = new(
			1.0,
			2.0,
			48.0,
			TimeSpan.FromMilliseconds(20),
			6,
			1000);

		Assert.That(
			curve.GetMultiplier(10),
			Is.EqualTo(Math.Pow(2.0, 20.0 / 768.0)).Within(1e-14));
	}
}
