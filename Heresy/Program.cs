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
        if (DesktopFilePickerPolicy.UseManagedDialogs(
            picker, OperatingSystem.IsLinux()))
            builder.UseManagedSystemDialogs();

        return builder;
    }
}
