using System;

using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FrameTimeTests
{
	[Test]
	public void EventBetweenFramesRoundsUpRatherThanEarly()
	{
		Assert.That(
			FrameTime.Ceiling(TimeSpan.FromTicks(1500), 10000),
			Is.EqualTo(2));
	}

	[Test]
	public void ExactFrameBoundaryIsNotAdvanced()
	{
		Assert.That(
			FrameTime.Ceiling(TimeSpan.FromMilliseconds(250), 4),
			Is.EqualTo(1));
	}

	[Test]
	public void FrameStartTimeRoundTripsThroughCeiling()
	{
		for (long frame = 0; frame < 1000; frame++)
		{
			TimeSpan time = FrameTime.FrameStartTime(frame, 48000);
			Assert.That(FrameTime.Ceiling(time, 48000), Is.EqualTo(frame));
		}
	}
}
