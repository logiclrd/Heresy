using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

using Heresy.UserInterface.Images;

namespace Heresy.UserInterface.Startup;

/// <summary>
/// A light, owned startup overlay. It does not initialize playback or
/// block the owner, and never owns the application's shutdown lifetime.
/// </summary>
public sealed class StartupSplashWindow : Window
{
	private readonly Window _owner;
	private readonly DispatcherTimer _timeout;
	private readonly StartupSplashDismissal _dismissal;

	public StartupSplashWindow(Window owner)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));

		WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Width = 800;
		Height = 280;
		Title = "Heresy";

		Content = new Border
		{
			Padding = new Thickness(24),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			Child = new Logo(),
		};

		_timeout = new DispatcherTimer
		{
			Interval = StartupSplashDismissal.Timeout,
		};
		_dismissal = new StartupSplashDismissal(
			() => _timeout.Stop(),
			() =>
			{
				if (IsVisible)
					Close();
			});
		_timeout.Tick += OnTimeout;
		_owner.Closed += OnOwnerClosed;
		Opened += OnOpened;
		Closed += OnClosed;

		// Tunnel through the logo so any pointer click or keypress dismisses,
		// even if a child control ever handles the corresponding routed event.
		AddHandler(InputElement.KeyDownEvent, OnAnyKeyDown,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed,
			RoutingStrategies.Tunnel, handledEventsToo: true);

		// This is an owned, nonmodal window: the user can still click or
		// type in its owner. Those interactions should dismiss the splash
		// too, without consuming the main window's input event.
		_owner.AddHandler(InputElement.KeyDownEvent, OnAnyKeyDown,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		_owner.AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed,
			RoutingStrategies.Tunnel, handledEventsToo: true);
	}

	private void OnOpened(object? sender, EventArgs e)
	{
		if (!_dismissal.IsDismissed)
		{
			_timeout.Start();
			Activate(); // Route the first keypress to the splash, not the song.
		}
	}

	private void OnAnyKeyDown(object? sender, KeyEventArgs e)
		=> _dismissal.Dismiss(StartupSplashDismissalReason.KeyPress);

	private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
		=> _dismissal.Dismiss(StartupSplashDismissalReason.PointerClick);

	private void OnTimeout(object? sender, EventArgs e)
		=> _dismissal.Dismiss(StartupSplashDismissalReason.Timeout);

	private void OnOwnerClosed(object? sender, EventArgs e)
		=> _dismissal.Dismiss(StartupSplashDismissalReason.OwnerClosed);

	private void OnClosed(object? sender, EventArgs e)
	{
		_dismissal.NotifyWindowClosed();
		_owner.Closed -= OnOwnerClosed;
		_owner.RemoveHandler(InputElement.KeyDownEvent, OnAnyKeyDown);
		_owner.RemoveHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed);
		_timeout.Tick -= OnTimeout;
		Opened -= OnOpened;
		Closed -= OnClosed;
	}
}
