using System;

using Avalonia;

namespace Heresy;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
		=> BuildAvaloniaApp()
			.StartWithClassicDesktopLifetime(args);

	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder
			.Configure<App>()
			.UsePlatformDetect()
			// The X11 portal chooser may appear on a different monitor from
			// its owner and give focus to another app when dismissed. Prefer
			// Avalonia's GTK chooser, which marks the dialog transient for
			// the main X11 window. Keep an opt-in portal override for
			// comparing provider behavior without rebuilding.
			.With(DesktopFilePickerPolicy.X11Options(
				Environment.GetEnvironmentVariable(
					DesktopFilePickerPolicy.EnvironmentVariable)));
}
