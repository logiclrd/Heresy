using System;
using System.Diagnostics;

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
	private readonly bool _isWayland;
	private readonly StartupSplashPlacementFollow _placementFollow = new();
	private readonly DispatcherTimer _nativeMoveWindow;
	private readonly DispatcherTimer _fadeTimer;
	private readonly Stopwatch _fadeElapsed = new();
	private bool _recenterPending;

	public StartupSplashWindow(Window owner)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));

		WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
		CanResize = false;
		ShowInTaskbar = false;
		_isWayland = StartupSplashPlacement.IsWayland(
			OperatingSystem.IsLinux(),
			Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
			Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
		// Always start with the compositor's center-screen hint. On
		// coordinate-capable desktops we'll move to the owner's actual
		// position after both windows have been mapped.
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Width = 800;
		Height = 280;
		Title = "Heresy";
		Opacity = 0d; // Hide provisional window-manager placement.

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
		_nativeMoveWindow = new DispatcherTimer
		{
			Interval = StartupSplashPlacementFollow.InitialMoveWindow,
		};
		_fadeTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(16),
		};
		_dismissal = new StartupSplashDismissal(
			() => _timeout.Stop(),
			() =>
			{
				if (IsVisible)
					Close();
			});
		_timeout.Tick += OnTimeout;
		_nativeMoveWindow.Tick += OnInitialMoveWindowElapsed;
		_fadeTimer.Tick += OnFadeTick;
		_owner.Closed += OnOwnerClosed;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
		_owner.PositionChanged += OnOwnerPositionChanged;
		_owner.Resized += OnOwnerResized;
		PositionChanged += OnSplashPositionChanged;
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
		if (_dismissal.IsDismissed)
			return;

		_placementFollow.Open();
		// Position after native creation, then follow early X11/Windows
		// configure notifications (including a compositor moving either
		// the owner or splash after the initial Show).
		if (!_isWayland)
		{
			TryCenterOverOwner();
			_nativeMoveWindow.Start();
		}

		_fadeElapsed.Restart();
		_fadeTimer.Start();
		_timeout.Start();
		Activate(); // Route the first keypress to the splash, not the song.
	}

	private void TryCenterOverOwner()
	{
		try
		{
			double scaling = RenderScaling > 0
				? RenderScaling : _owner.RenderScaling;
			PixelSize ownerSize = StartupSplashPlacement.LogicalSizeInPixels(
				_owner.Bounds.Width, _owner.Bounds.Height,
				_owner.RenderScaling);
			PixelRect ownerRect = new(_owner.Position, ownerSize);
			PixelRect workArea =
				_owner.Screens.ScreenFromWindow(_owner)?.WorkingArea
					?? ownerRect;
			PixelRect anchor = StartupSplashPlacement.OwnerOrScreenAnchor(
				ownerRect, workArea,
				_owner.WindowState == WindowState.Maximized);
			PixelSize splashSize = StartupSplashPlacement.LogicalSizeInPixels(
				Bounds.Width > 0 ? Bounds.Width : Width,
				Bounds.Height > 0 ? Bounds.Height : Height,
				scaling);
			PixelPoint target = StartupSplashPlacement.CenterOver(anchor, splashSize);
			if (Position != target)
				Position = target;
		}
		catch (Exception exception)
		{
			// Some backends expose coordinates but refuse window moves.
			// The initial CenterScreen hint remains our nonfatal fallback.
			Debug.WriteLine($"Splash explicit placement unavailable: {exception}");
		}
	}

	private void OnOwnerPropertyChanged(
		object? sender,
		AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == Window.WindowStateProperty)
			OnPlacementChanged(StartupSplashPositionChange.OwnerWindowState);
	}

	private void OnOwnerPositionChanged(object? sender, PixelPointEventArgs e)
		=> OnPlacementChanged(StartupSplashPositionChange.OwnerMoved);

	private void OnOwnerResized(object? sender, WindowResizedEventArgs e)
		=> OnPlacementChanged(StartupSplashPositionChange.OwnerResized);

	private void OnSplashPositionChanged(object? sender, PixelPointEventArgs e)
		=> OnPlacementChanged(StartupSplashPositionChange.SplashMoved);

	private void OnPlacementChanged(StartupSplashPositionChange change)
	{
		if (!_placementFollow.ShouldRecenter(!_isWayland, change)
			|| _dismissal.IsDismissed || _recenterPending)
			return;

		// A native ConfigureNotify can arrive just after Opened, and the
		// window manager may also reposition the splash itself. Coalesce
		// notifications and read the latest physical owner coordinates.
		_recenterPending = true;
		Dispatcher.UIThread.Post(() =>
		{
			_recenterPending = false;
			if (IsVisible && !_dismissal.IsDismissed)
				TryCenterOverOwner();
		}, DispatcherPriority.Loaded);
	}

	private void OnInitialMoveWindowElapsed(object? sender, EventArgs e)
	{
		_nativeMoveWindow.Stop();
		_placementFollow.EndInitialMoveWindow();
	}

	private void OnFadeTick(object? sender, EventArgs e)
	{
		Opacity = StartupSplashPlacementFollow.OpacityAt(_fadeElapsed.Elapsed);
		if (Opacity >= 1d)
		{
			_fadeTimer.Stop();
			_fadeElapsed.Stop();
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
		_placementFollow.Close();
		_nativeMoveWindow.Stop();
		_fadeTimer.Stop();
		_fadeElapsed.Stop();
		_owner.Closed -= OnOwnerClosed;
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
		_owner.PositionChanged -= OnOwnerPositionChanged;
		_owner.Resized -= OnOwnerResized;
		PositionChanged -= OnSplashPositionChanged;
		_owner.RemoveHandler(InputElement.KeyDownEvent, OnAnyKeyDown);
		_owner.RemoveHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed);
		_timeout.Tick -= OnTimeout;
		_nativeMoveWindow.Tick -= OnInitialMoveWindowElapsed;
		_fadeTimer.Tick -= OnFadeTick;
		Opened -= OnOpened;
		Closed -= OnClosed;
	}
}
