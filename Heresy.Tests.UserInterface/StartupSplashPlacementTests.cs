using Avalonia;

using Heresy.UserInterface.Startup;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class StartupSplashPlacementTests
{
	[Test]
	public void CentersOverNormalOwnerOnNonPrimaryMonitor()
	{
		PixelRect owner = new(-1600, 120, 1200, 760);
		PixelSize splash = new(800, 280);
		Assert.That(StartupSplashPlacement.CenterOver(owner, splash),
			Is.EqualTo(new PixelPoint(-1400, 360)));
	}

	[Test]
	public void CentersOverMaximizedOwnersActualWorkArea()
	{
		PixelRect restoreBounds = new(80, 90, 1100, 720);
		PixelRect secondaryWorkArea = new(1920, 0, 2560, 1370);
		PixelRect anchor = StartupSplashPlacement.OwnerOrScreenAnchor(
			restoreBounds, secondaryWorkArea, isMaximized: true);
		Assert.That(anchor, Is.EqualTo(secondaryWorkArea));
		Assert.That(StartupSplashPlacement.CenterOver(anchor, new(800, 280)),
			Is.EqualTo(new PixelPoint(2800, 545)));
		Assert.That(StartupSplashPlacement.OwnerOrScreenAnchor(
			restoreBounds, secondaryWorkArea, isMaximized: false),
			Is.EqualTo(restoreBounds));
	}

	[Test]
	public void ScalingConvertsLogicalSplashAndOwnerToPhysicalPixelCoordinates()
	{
		PixelSize splash = StartupSplashPlacement.LogicalSizeInPixels(
			800, 280, 1.5);
		Assert.That(splash, Is.EqualTo(new PixelSize(1200, 420)));
		PixelRect owner = new(2100, 200, 1800, 1140);
		Assert.That(StartupSplashPlacement.CenterOver(owner, splash),
			Is.EqualTo(new PixelPoint(2400, 560)));
	}

	[TestCase(true, "wayland", "", true)]
	[TestCase(true, "Wayland", null, true)]
	[TestCase(true, "x11", "wayland-0", true)]
	[TestCase(true, "x11", null, false)]
	[TestCase(false, "wayland", "wayland-0", false)]
	public void WaylandDetectionOnlySuppressesPlacementWhereRelevant(
		bool isLinux, string? session, string? display, bool expected)
		=> Assert.That(StartupSplashPlacement.IsWayland(
			isLinux, session, display), Is.EqualTo(expected));
}
