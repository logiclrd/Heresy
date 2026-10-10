using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.SDL;
using Heresy.UserInterface;

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
			desktop.MainWindow =
				new MainWindow(
					new LazySongPlaybackTransport(
						() => CreatePlaybackTransport(settings)),
					settings);
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
