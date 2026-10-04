using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerVibratoTests
{
	[Test]
	public void SpeedAdvancesBytePhaseByFourTimesSpeed()
	{
		Assert.That(TrackerVibrato.AdvancePhase(0, 5), Is.EqualTo(20));
		Assert.That(TrackerVibrato.AdvancePhase(250, 3), Is.EqualTo(6));
	}

	[Test]
	public void FineSineAndDepthProduceImpulseTrackerLinearSlideUnits()
	{
		// phase 20 => FineSineData[20] == 30.
		Assert.That(TrackerVibrato.GetLinearSlideUnits(20, 3), Is.EqualTo(6));

		// phase 64 is the positive peak; depth 15 reaches +60 units.
		Assert.That(TrackerVibrato.GetLinearSlideUnits(64, 15), Is.EqualTo(60));

		// phase 192 is the negative peak.
		Assert.That(TrackerVibrato.GetLinearSlideUnits(192, 15), Is.EqualTo(-60));
	}

	[Test]
	public void LinearSlideUnitsMapTo768UnitsPerOctave()
	{
		double multiplier = TrackerVibrato.GetPitchMultiplier(64, 15);

		Assert.That(multiplier, Is.EqualTo(Math.Pow(2.0, 60.0 / 768.0)).Within(1e-14));
	}
}
