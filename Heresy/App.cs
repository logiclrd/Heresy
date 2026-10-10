using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.SDL;
using Heresy.UserInterface;
using Heresy.UserInterface.Startup;

namespace Heresy;

public sealed class App : Application
{
	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// Realtime and export both capture immutable configuration from
			// the same UI-owned selection when a new render starts.
			AudioOutputSettings settings = new();
			MainWindow mainWindow =
				new(
					new LazySongPlaybackTransport(
						() => CreatePlaybackTransport(settings)),
					settings);
			desktop.MainWindow = mainWindow;

			// The persisted maximization preference is local desktop UI
			// state, never song data. The platform may ignore pre-Show
			// requests, so reaffirm it on Opened before showing the splash.
			MainWindowStatePreference windowPreference =
				MainWindowStatePreference.ForCurrentUser();
			bool? restoredMaximized = windowPreference.Read();
			MainWindowMaximizedTracker maximizedTracker =
				new(restoredMaximized ?? false);
			if (restoredMaximized == true)
				mainWindow.WindowState = WindowState.Maximized;
			mainWindow.Opened += (_, _) =>
			{
				if (restoredMaximized is bool persisted)
				{
					WindowState desired = persisted
						? WindowState.Maximized : WindowState.Normal;
					if (mainWindow.WindowState != desired)
						mainWindow.WindowState = desired;
				}

				// Observe actual window-manager transitions, not the
				// initial restoration or transient minimized/fullscreen
				// states. A maximization toggle is written immediately.
				mainWindow.PropertyChanged += (_, change) =>
				{
					if (change.Property != Window.WindowStateProperty)
						return;
					bool? value = mainWindow.WindowState switch
					{
						WindowState.Maximized => true,
						WindowState.Normal => false,
						_ => null,
					};
					bool? update = maximizedTracker.Record(value);
					if (update is bool maximized)
						windowPreference.TrySave(maximized);
				};
			};

			// The main window remains the application's actual main window;
			// only open the owned, chromeless splash after it has appeared.
			// Dispatcher-posting avoids reentering the initial Show sequence.
			bool splashScheduled = false;
			mainWindow.Opened += (_, _) =>
			{
				if (splashScheduled)
					return;
				splashScheduled = true;
				Dispatcher.UIThread.Post(() =>
				{
					if (mainWindow.IsVisible)
						new StartupSplashWindow(mainWindow).Show(mainWindow);
				}, DispatcherPriority.Loaded);
			};
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static ISongPlaybackTransport CreatePlaybackTransport(
		AudioOutputSettings settings)
	{
		PlaybackRequestAudioSourceFactory sourceFactory =
			new(() => settings.Current);
		SdlAudioOutputBackend backend = new();

		return new SongPlaybackTransport(
			backend,
			sourceFactory);
	}
}
