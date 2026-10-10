using System;

using Avalonia;

namespace Heresy.UserInterface.Startup;

/// <summary>
/// On desktops that expose top-level coordinates, center the splash over
/// its owner in physical pixels. A maximized owner's visible screen work
/// area is the anchor, avoiding border offsets and stale restore bounds.
/// On Wayland only compositor-driven location hints are meaningful.
/// </summary>
public static class StartupSplashPlacement
{
	public static bool IsWayland(
		bool isLinux, string? sessionType, string? waylandDisplay)
		=> isLinux
			&& (string.Equals(sessionType, "wayland",
				StringComparison.OrdinalIgnoreCase)
				|| !string.IsNullOrWhiteSpace(waylandDisplay));

	public static PixelPoint CenterOver(
		PixelRect anchor, PixelSize splashSize)
	{
		if (anchor.Width <= 0 || anchor.Height <= 0
			|| splashSize.Width <= 0 || splashSize.Height <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(anchor));
		}
		return new PixelPoint(
			checked(anchor.X + (anchor.Width - splashSize.Width) / 2),
			checked(anchor.Y + (anchor.Height - splashSize.Height) / 2));
	}

	public static PixelRect OwnerOrScreenAnchor(
		PixelRect ownerBounds, PixelRect screenWorkArea,
		bool isMaximized)
		=> isMaximized && screenWorkArea.Width > 0
			&& screenWorkArea.Height > 0
				? screenWorkArea : ownerBounds;

	public static PixelSize LogicalSizeInPixels(
		double width, double height, double scaling)
	{
		if (!(width > 0) || !(height > 0)
			|| !(scaling > 0) || !double.IsFinite(width)
			|| !double.IsFinite(height) || !double.IsFinite(scaling))
		{
			throw new ArgumentOutOfRangeException(nameof(scaling));
		}
		return new PixelSize(
			checked((int)Math.Round(width * scaling)),
			checked((int)Math.Round(height * scaling)));
	}
}
