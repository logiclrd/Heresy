using System;

namespace Heresy.UserInterface.Startup;

/// <summary>
/// Tracks the short period in which a native window manager may relocate
/// newly mapped windows. Separate from the splash's four-second lifetime.
/// The owner may still maximize later, which requires a new placement.
/// </summary>
public sealed class StartupSplashPlacementFollow
{
	public static readonly TimeSpan InitialMoveWindow = TimeSpan.FromMilliseconds(50);
	public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(250);

	private bool _open;
	private bool _watchingInitialMoves;

	public void Open()
	{
		_open = true;
		_watchingInitialMoves = true;
	}

	public void EndInitialMoveWindow() => _watchingInitialMoves = false;

	public void Close()
	{
		_open = false;
		_watchingInitialMoves = false;
	}

	public bool ShouldRecenter(bool canSetPosition, StartupSplashPositionChange change)
		=> _open && canSetPosition
			&& (change == StartupSplashPositionChange.OwnerWindowState
				|| _watchingInitialMoves);

	public static double OpacityAt(TimeSpan elapsed)
		=> Math.Clamp(elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds,
			0d, 1d);
}

public enum StartupSplashPositionChange
{
	OwnerMoved,
	OwnerResized,
	SplashMoved,
	OwnerWindowState,
}
