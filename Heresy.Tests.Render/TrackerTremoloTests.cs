using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerTremoloTests
{
	[Test]
	public void DepthMatchesImpulseTrackerVolumeUnits()
	{
		Assert.That(
			TrackerTremolo.GetVolumeOffsetUnits(64, 3),
			Is.EqualTo(6));
		Assert.That(
			TrackerTremolo.GetVolumeOffsetUnits(192, 3),
			Is.EqualTo(-6));

		Assert.That(
			TrackerTremolo.GetVolumeOffsetUnits(64, 15),
			Is.EqualTo(30));
		Assert.That(
			TrackerTremolo.GetVolumeOffsetUnits(192, 15),
			Is.EqualTo(-30));
	}

	[Test]
	public void ContinuousTremoloInterpolatesBetweenLegacyPhaseAnchors()
	{
		double at20 =
			TrackerTremolo.GetContinuousVolumeOffsetUnits(20.0, 5);
		double at21 =
			TrackerTremolo.GetContinuousVolumeOffsetUnits(21.0, 5);
		double between =
			TrackerTremolo.GetContinuousVolumeOffsetUnits(20.5, 5);

		Assert.That(
			at20,
			Is.EqualTo(TrackerTremolo.GetVolumeOffsetUnits(20, 5)));
		Assert.That(
			at21,
			Is.EqualTo(TrackerTremolo.GetVolumeOffsetUnits(21, 5)));
		Assert.That(
			between,
			Is.EqualTo((at20 + at21) / 2.0).Within(1e-14));
	}
}
