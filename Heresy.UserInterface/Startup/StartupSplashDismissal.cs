using System;

namespace Heresy.UserInterface.Startup;

/// <summary>
/// One-shot lifecycle for a temporary splash. All calls run on the Avalonia
/// UI thread. Closing the owner, timeout, and user input compete to dismiss
/// the same window; a synchronous Closed event cannot close it twice.
/// </summary>
public sealed class StartupSplashDismissal
{
	public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(4);

	private readonly Action _stopTimer;
	private readonly Action _closeWindow;

	public StartupSplashDismissal(Action stopTimer, Action closeWindow)
	{
		_stopTimer = stopTimer
			?? throw new ArgumentNullException(nameof(stopTimer));
		_closeWindow = closeWindow
			?? throw new ArgumentNullException(nameof(closeWindow));
	}

	public bool IsDismissed { get; private set; }
	public StartupSplashDismissalReason? Reason { get; private set; }

	public void Dismiss(StartupSplashDismissalReason reason)
	{
		if (IsDismissed)
			return;
		IsDismissed = true;
		Reason = reason;
		_stopTimer();
		_closeWindow();
	}

	/// <summary>
	/// A platform-initiated close or owner shutdown must not request Close
	/// again. It still stops the pending timeout.
	/// </summary>
	public void NotifyWindowClosed()
	{
		if (IsDismissed)
			return;
		IsDismissed = true;
		Reason = StartupSplashDismissalReason.ExternalClose;
		_stopTimer();
	}
}

public enum StartupSplashDismissalReason
{
	KeyPress,
	PointerClick,
	Timeout,
	OwnerClosed,
	ExternalClose,
}
