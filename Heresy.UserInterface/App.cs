using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.SDL;

namespace Heresy.UserInterface;

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
			desktop.MainWindow =
				new MainWindow(
					new LazySongPlaybackTransport(
						CreatePlaybackTransport));
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static ISongPlaybackTransport CreatePlaybackTransport()
	{
		RenderConfiguration configuration =
			RenderConfiguration.Stereo(
				sampleRate: 48000);
		WaveSampleDataProvider samples = new();
		PlaybackRequestAudioSourceFactory sourceFactory =
			new(
				configuration,
				samples);
		SdlAudioOutputBackend backend = new();

		return new SongPlaybackTransport(
			backend,
			sourceFactory);
	}
}
