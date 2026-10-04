using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PitchTrajectoryTests
{
	[Test]
	public void IntegratesPiecewiseConstantPitchMultipliers()
	{
		PitchTrajectory trajectory = new();
		trajectory.SetMultiplier(0, 2.0);
		trajectory.SetMultiplier(4, 0.5);

		Assert.That(trajectory.GetPosition(0), Is.EqualTo(0.0));
		Assert.That(trajectory.GetPosition(2), Is.EqualTo(4.0));
		Assert.That(trajectory.GetPosition(4), Is.EqualTo(8.0));
		Assert.That(trajectory.GetPosition(6), Is.EqualTo(9.0));
	}

	[Test]
	public void FindsFirstFrameWhichReachesIntegratedPosition()
	{
		PitchTrajectory trajectory = new();
		trajectory.SetMultiplier(0, 2.0);
		trajectory.SetMultiplier(4, 0.5);

		Assert.That(trajectory.FindFrameAtOrAfterPosition(0.0), Is.EqualTo(0));
		Assert.That(trajectory.FindFrameAtOrAfterPosition(7.9), Is.EqualTo(4));
		Assert.That(trajectory.FindFrameAtOrAfterPosition(8.0), Is.EqualTo(4));
		Assert.That(trajectory.FindFrameAtOrAfterPosition(9.0), Is.EqualTo(6));
	}

	[Test]
	public void SameFrameControlPointReplacesMultiplierWithoutChangingPosition()
	{
		PitchTrajectory trajectory = new();
		trajectory.SetMultiplier(0, 2.0);
		trajectory.SetMultiplier(4, 0.5);
		trajectory.SetMultiplier(4, 1.0);

		Assert.That(trajectory.GetPosition(4), Is.EqualTo(8.0));
		Assert.That(trajectory.GetPosition(5), Is.EqualTo(9.0));
	}
}
