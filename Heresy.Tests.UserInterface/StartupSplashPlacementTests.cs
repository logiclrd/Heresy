using System;

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
	[TestCase(true, "x11", "wayland-0", false)]
	[TestCase(true, "", "wayland-0", true)]
	[TestCase(true, "x11", null, false)]
	[TestCase(false, "wayland", "wayland-0", false)]
	public void WaylandDetectionOnlySuppressesPlacementWhereRelevant(
		bool isLinux, string? session, string? display, bool expected)
		=> Assert.That(StartupSplashPlacement.IsWayland(
			isLinux, session, display), Is.EqualTo(expected));
	[Test]
	public void NativeOwnerAndSplashMovesAreFollowedOnlyDuringFirst50Milliseconds()
	{
		StartupSplashPlacementFollow follow = new();
		Assert.That(StartupSplashPlacementFollow.InitialMoveWindow,
			Is.EqualTo(TimeSpan.FromMilliseconds(50)));
		Assert.That(follow.ShouldRecenter(true, StartupSplashPositionChange.OwnerMoved),
			Is.False, "No placement should be queued before Opened.");

		follow.Open();
		foreach (StartupSplashPositionChange change in new[]
		{
			StartupSplashPositionChange.OwnerMoved,
			StartupSplashPositionChange.OwnerResized,
			StartupSplashPositionChange.SplashMoved,
			StartupSplashPositionChange.OwnerWindowState,
		})
		{
			Assert.That(follow.ShouldRecenter(true, change), Is.True,
				change.ToString());
			Assert.That(follow.ShouldRecenter(false, change), Is.False,
				"Wayland must not attempt direct positioning.");
		}

		follow.EndInitialMoveWindow();
		Assert.Multiple(() =>
		{
			Assert.That(follow.ShouldRecenter(true,
				StartupSplashPositionChange.OwnerMoved), Is.False);
			Assert.That(follow.ShouldRecenter(true,
				StartupSplashPositionChange.OwnerResized), Is.False);
			Assert.That(follow.ShouldRecenter(true,
				StartupSplashPositionChange.SplashMoved), Is.False);
			Assert.That(follow.ShouldRecenter(true,
				StartupSplashPositionChange.OwnerWindowState), Is.True,
				"Late maximization still changes the correct centering anchor.");
		});

		follow.Close();
		Assert.That(follow.ShouldRecenter(true,
			StartupSplashPositionChange.OwnerWindowState), Is.False,
			"Closed splash must not be moved by queued notifications.");
	}

	[TestCase(-20, 0)]
	[TestCase(0, 0)]
	[TestCase(50, 0.2)]
	[TestCase(125, 0.5)]
	[TestCase(250, 1)]
	[TestCase(500, 1)]
	public void SplashFadeIsLinearAndClampedTo250Milliseconds(
		int elapsedMilliseconds, double expected)
	{
		Assert.That(StartupSplashPlacementFollow.FadeDuration,
			Is.EqualTo(TimeSpan.FromMilliseconds(250)));
		Assert.That(StartupSplashPlacementFollow.OpacityAt(
			TimeSpan.FromMilliseconds(elapsedMilliseconds)),
			Is.EqualTo(expected).Within(1e-12));
	}

}
