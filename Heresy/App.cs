using Avalonia;
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
