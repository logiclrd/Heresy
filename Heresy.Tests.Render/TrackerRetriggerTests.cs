using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerRetriggerTests
{
	[TestCase(0x0, 32)]
	[TestCase(0x1, 31)]
	[TestCase(0x2, 30)]
	[TestCase(0x3, 28)]
	[TestCase(0x4, 24)]
	[TestCase(0x5, 16)]
	[TestCase(0x6, 21)]
	[TestCase(0x7, 16)]
	[TestCase(0x8, 32)]
	[TestCase(0x9, 33)]
	[TestCase(0xA, 34)]
	[TestCase(0xB, 36)]
	[TestCase(0xC, 40)]
	[TestCase(0xD, 48)]
	[TestCase(0xE, 48)]
	[TestCase(0xF, 64)]
	public void VolumeTransformMatchesItIntegerArithmetic(
		byte transform,
		int expectedUnits)
	{
		double result =
			TrackerRetrigger.ApplyVolumeTransform(
				32.0 / 64.0,
				transform);

		Assert.That(
			result,
			Is.EqualTo(expectedUnits / 64.0).Within(1e-14));
	}

	[Test]
	public void VolumeTransformClampsAtTrackerLimits()
	{
		Assert.That(
			TrackerRetrigger.ApplyVolumeTransform(0.0, 0x5),
			Is.EqualTo(0.0));
		Assert.That(
			TrackerRetrigger.ApplyVolumeTransform(1.0, 0xD),
			Is.EqualTo(1.0));
	}
}
