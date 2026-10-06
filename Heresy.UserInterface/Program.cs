using System;

using Avalonia;

namespace Heresy.UserInterface;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
		=> BuildAvaloniaApp()
			.StartWithClassicDesktopLifetime(args);

	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder
			.Configure<App>()
			.UsePlatformDetect();
}
