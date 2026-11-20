using System;

using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongTreeDragAutoScrollTests
{
    [Test]
    public void ScrollingBeginsOnlyOutsideViewportAndIsProportionalToDistance()
    {
        Assert.That(SongTreeDragAutoScroll.Velocity(100, 100, 200), Is.Zero);
        Assert.That(SongTreeDragAutoScroll.Velocity(300, 100, 200), Is.Zero);
        Assert.That(SongTreeDragAutoScroll.Velocity(220, 100, 200), Is.Zero);
        Assert.That(SongTreeDragAutoScroll.Velocity(90, 100, 200),
            Is.EqualTo(-80));
        Assert.That(SongTreeDragAutoScroll.Velocity(80, 100, 200),
            Is.EqualTo(-160));
        Assert.That(SongTreeDragAutoScroll.Velocity(310, 100, 200),
            Is.EqualTo(80));
        Assert.That(SongTreeDragAutoScroll.Velocity(320, 100, 200),
            Is.EqualTo(160));
    }

    [Test]
    public void VelocityIsBoundedAndRejectsInvalidViewports()
    {
        Assert.That(SongTreeDragAutoScroll.Velocity(-1000, 0, 300),
            Is.EqualTo(-SongTreeDragAutoScroll.MaximumSpeedDipsPerSecond));
        Assert.That(SongTreeDragAutoScroll.Velocity(1000, 0, 300),
            Is.EqualTo(SongTreeDragAutoScroll.MaximumSpeedDipsPerSecond));
        Assert.That(SongTreeDragAutoScroll.Velocity(20, 0, 0), Is.Zero);
        Assert.That(SongTreeDragAutoScroll.Velocity(double.NaN, 0, 300), Is.Zero);
    }

    [Test]
    public void ScrollOffsetTracksElapsedTimeAndClampsBothEnds()
    {
        Assert.That(SongTreeDragAutoScroll.AdvanceOffset(200, 900, 300, 160, 0.05),
            Is.EqualTo(208).Within(0.000001));
        Assert.That(SongTreeDragAutoScroll.AdvanceOffset(3, 900, 300, -160, 0.05),
            Is.Zero);
        Assert.That(SongTreeDragAutoScroll.AdvanceOffset(598, 900, 300, 160, 0.05),
            Is.EqualTo(600));
        Assert.That(SongTreeDragAutoScroll.AdvanceOffset(20, 900, 300, 160, -10),
            Is.EqualTo(20));
        Assert.That(SongTreeDragAutoScroll.AdvanceOffset(20, 900, 300, 160, 100),
            Is.EqualTo(36), "elapsed intervals are capped after scheduler stalls");
    }
}
