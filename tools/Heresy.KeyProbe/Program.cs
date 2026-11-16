using System;
using System.Globalization;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Heresy.KeyProbe;

internal sealed record ProbeOptions(string Label, string Output)
{
    public static ProbeOptions Parse(string[] args)
    {
        string label = "unspecified";
        string? output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--label" or "--output" && i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {args[i]}");
            switch (args[i])
            {
                case "--label": label = args[++i]; break;
                case "--output": output = args[++i]; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        if (label.Length == 0)
            throw new ArgumentException("Session label cannot be empty.");
        string safe = new(label.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        output ??= $"keyprobe-{safe}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        return new ProbeOptions(label, Path.GetFullPath(output));
    }
}

internal static class Program
{
    internal static ProbeOptions Options { get; private set; } = new("unspecified", "");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Avalonia/X11 key event diagnostic probe");
            Console.WriteLine("Usage: dotnet run --project tools/Heresy.KeyProbe -- --label xorg [--output ./xorg.log]");
            Console.WriteLine("       dotnet run --project tools/Heresy.KeyProbe -- --label xephyr [--output ./xephyr.log]");
            Console.WriteLine("Records only the designated test fields. Output logs contain the text typed there.");
            return 0;
        }
        try
        {
            Options = ProbeOptions.Parse(args);
            if (File.Exists(Options.Output))
                throw new IOException($"Refusing to overwrite existing log: {Options.Output}");
            return AppBuilder.Configure<ProbeApp>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ProbeWindow(Program.Options);
        base.OnFrameworkInitializationCompleted();
    }
}
