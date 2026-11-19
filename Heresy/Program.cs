using System;

using Avalonia;
using Avalonia.Dialogs;

namespace Heresy;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        string? picker = Environment.GetEnvironmentVariable(
            DesktopFilePickerPolicy.EnvironmentVariable);

        AppBuilder builder = AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            // GTK (default on X11) creates an owner-transient dialog;
            // "portal" restores the desktop-hosted portal picker.
            .With(DesktopFilePickerPolicy.X11Options(picker));

        // The Avalonia-managed chooser includes a permanently visible
        // filename field and uses ShowDialog(owner) for window ownership.
        // Its registration overrides the platform storage provider, so
        // make this a Linux-only opt-in for direct GTK/managed comparison.
        // Keep the OS guard explicit at the call site: the Avalonia
        // extension is annotated for desktop operating systems, and CA1416
        // cannot infer OS reachability through our policy helper.
        if (OperatingSystem.IsLinux()
            && DesktopFilePickerPolicy.UseManagedDialogs(picker, isLinux: true))
            builder.UseManagedSystemDialogs();

        return builder;
    }
}
