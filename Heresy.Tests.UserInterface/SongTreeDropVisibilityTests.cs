using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongTreeDropVisibilityTests
{
    [TestCase(120, 100, 600, 120, 20, 120)]
    [TestCase(120, 100, 600, 140, 40, 120)]
    [TestCase(120, 100, 600, 195, 25, 120)]
    [TestCase(120, 100, 600, 110, 20, 110)]
    [TestCase(120, 100, 600, 220, 20, 140)]
    [TestCase(120, 100, 600, 119, 20, 119)]
    [TestCase(120, 100, 600, 199, 22, 121)]
    [TestCase(200, 100, 240, 5, 20, 5)]
    [TestCase(200, 100, 240, 225, 15, 140)]
    [TestCase(0, 300, 200, 80, 25, 0)]
    public void ScrollsOnlyByMinimumNeededToShowEntireMovedRow(
        double original, double viewport, double extent,
        double rowTop, double rowHeight, double expected)
    {
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            original, viewport, extent, rowTop, rowHeight),
            Is.EqualTo(expected).Within(0.000001));
    }

    [Test]
    public void OversizedRowsUseNearestEdgeWithoutOscillating()
    {
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            100, 80, 500, 125, 120), Is.EqualTo(125));
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            200, 80, 500, 70, 120), Is.EqualTo(110));
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            135, 80, 500, 100, 120), Is.EqualTo(135));
    }

    [Test]
    public void InvalidGeometryDoesNotInventScrollMovement()
    {
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            100, 0, 500, 20, 20), Is.EqualTo(100));
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            100, 80, double.NaN, 20, 20), Is.EqualTo(100));
        Assert.That(SongTreeDropVisibility.MinimalOffset(
            100, 80, 500, 20, double.NaN), Is.EqualTo(100));
    }
}
