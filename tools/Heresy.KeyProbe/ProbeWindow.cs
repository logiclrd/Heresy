using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Heresy.KeyProbe;

/// <summary>
/// Diagnostics only. Events are observed and never marked handled, repaired,
/// or replayed. No user text is sampled outside the two explicit test fields.
/// The saved log is append-only during the session (including snapshots).
/// </summary>
internal sealed class ProbeWindow : Window
{
    private readonly ProbeOptions _options;
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StringBuilder _visible = new();
    private readonly TextBox _single;
    private readonly TextBox _multiline;
    private readonly TextBox _logView;
    private bool _refreshQueued;
    private int _serial;
    private PhysicalKey _lastPhysicalKey = PhysicalKey.None;

    public ProbeWindow(ProbeOptions options)
    {
        _options = options;
        _writer = new StreamWriter(new FileStream(options.Output,
            FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        Title = $"Avalonia/X11 Key Probe — {options.Label}";
        Width = 1050;
        Height = 790;
        MinWidth = 690;
        MinHeight = 560;

        _single = new TextBox
        {
            Name = "SingleLineTest",
            Watermark = "Click here and type apostrophe, comma, period, semicolon; repeat with Caps / Shift",
            FontSize = 18,
        };
        _multiline = new TextBox
        {
            Name = "MultilineTest",
            Watermark = "Second ordinary TextBox: try the same keys here",
            AcceptsReturn = true,
            MinHeight = 65,
            FontSize = 16,
        };
        _logView = new TextBox
        {
            Name = "EventLog",
            Text = "",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
        };

        Button snapshot = new()
        {
            Content = "Snapshot native XKB state / full keymap",
        };
        snapshot.Click += (_, _) => LogBlock("Native snapshot (manual)", X11Snapshot.FullSnapshot());

        Button reset = new()
        {
            Content = "Clear the two test fields",
        };
        reset.Click += (_, _) =>
        {
            _single.Text = "";
            _multiline.Text = "";
            _single.Focus();
            Log("USER action: cleared test fields (saved event log retained)");
        };

        TextBlock heading = new()
        {
            Text = "Avalonia 12.1.3 / native X11 diagnostic — NO input corrections",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
        };
        TextBlock directions = new()
        {
            Text = "Focus one of the two test boxes. Press ' , . ; with Caps OFF, " +
                "Caps ON, then Caps ON + physical Shift. Compare the two saved logs " +
                "from direct Xorg and nested Xephyr. Only input to these two fields is recorded.",
            TextWrapping = TextWrapping.Wrap,
        };
        TextBlock output = new()
        {
            Text = $"Session: {options.Label}  |  Log file: {options.Output}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        TextBlock note = new()
        {
            Text = "Captured: tunneling/target/bubbling key events, KeySymbol, TextInput " +
                "Unicode, caret/selection/text changes, XkbGetState at dispatch, " +
                "X11 server/layout snapshot. No X11 key events are consumed.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        StackPanel header = new()
        {
            Spacing = 7,
        };
        header.Children.Add(heading);
        header.Children.Add(directions);
        header.Children.Add(output);
        header.Children.Add(note);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        actions.Children.Add(snapshot);
        actions.Children.Add(reset);

        Grid grid = new()
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            RowSpacing = 10,
        };
        grid.Children.Add(header);
        Grid.SetRow(_single, 1);
        grid.Children.Add(_single);
        Grid.SetRow(_multiline, 2);
        grid.Children.Add(_multiline);
        Grid.SetRow(actions, 3);
        grid.Children.Add(actions);
        Grid.SetRow(_logView, 4);
        grid.Children.Add(_logView);
        Content = grid;

        // Both phases are observable even if TextBox normally marks a key
        // handled. We do not mutate either the input args or their Handled bit.
        AddHandler(InputElement.KeyDownEvent,
            (_, e) => OnKey("window/tunnel", "KeyDown", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent,
            (_, e) => OnKey("window/tunnel", "KeyUp", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.TextInputEvent,
            (_, e) => OnTextInput("window/tunnel", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        AddHandler(InputElement.KeyDownEvent,
            (_, e) => OnKey("window/bubble", "KeyDown", e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent,
            (_, e) => OnKey("window/bubble", "KeyUp", e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.TextInputEvent,
            (_, e) => OnTextInput("window/bubble", e),
            RoutingStrategies.Bubble, handledEventsToo: true);

        ObserveTestField(_single);
        ObserveTestField(_multiline);
        Closed += (_, _) =>
        {
            Log("Window closed");
            _writer.Dispose();
        };

        Log($"START session={Quote(options.Label)}");
        Log($"Runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; processArch={RuntimeInformation.ProcessArchitecture}");
        Log($"Avalonia={typeof(Application).Assembly.GetName().Version}; culture={CultureInfo.CurrentCulture.Name}");
        LogBlock("Native X11 startup snapshot", X11Snapshot.FullSnapshot());
    }

    private bool HasTestFocus()
        => _single.IsFocused || _multiline.IsFocused;

    private string Target()
        => _single.IsFocused ? _single.Name ?? "single"
            : _multiline.IsFocused ? _multiline.Name ?? "multiline" : "<none>";

    private void ObserveTestField(TextBox box)
    {
        box.AddHandler(InputElement.KeyDownEvent,
            (_, e) => OnKey(box.Name + "/tunnel", "KeyDown", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        box.AddHandler(InputElement.KeyUpEvent,
            (_, e) => OnKey(box.Name + "/tunnel", "KeyUp", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        box.AddHandler(InputElement.TextInputEvent,
            (_, e) => OnTextInput(box.Name + "/tunnel", e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        box.AddHandler(InputElement.KeyDownEvent,
            (_, e) => OnKey(box.Name + "/bubble", "KeyDown", e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        box.AddHandler(InputElement.KeyUpEvent,
            (_, e) => OnKey(box.Name + "/bubble", "KeyUp", e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        box.AddHandler(InputElement.TextInputEvent,
            (_, e) => OnTextInput(box.Name + "/bubble", e),
            RoutingStrategies.Bubble, handledEventsToo: true);
        box.TextChanged += (_, _) =>
        {
            if (HasTestFocus())
                Log($"TEXT_CHANGED box={box.Name} text={Quote(box.Text)} codepoints={Scalars(box.Text)}" +
                    $" caret={box.CaretIndex} selection={box.SelectionStart}:{box.SelectionEnd}");
        };
        box.GotFocus += (_, _) => Log($"GOT_FOCUS box={box.Name}");
        box.LostFocus += (_, _) => Log($"LOST_FOCUS box={box.Name}");
    }

    private void OnKey(string route, string kind, KeyEventArgs e)
    {
        if (!HasTestFocus()) return;
        if (route == "window/tunnel" && kind == "KeyDown")
            _lastPhysicalKey = e.PhysicalKey;

        Log($"{kind} route={route} focused={Target()} source={e.Source?.GetType().Name ?? "<null>"}" +
            $" Key={e.Key} PhysicalKey={e.PhysicalKey} KeySymbol={Quote(e.KeySymbol)}" +
            $" KeySymbolScalars={Scalars(e.KeySymbol)} Modifiers={e.KeyModifiers}" +
            $" DeviceType={e.KeyDeviceType} Handled={e.Handled}" +
            $" | {X11Snapshot.State()}");

        if (route == "window/bubble" && kind == "KeyUp" &&
            _lastPhysicalKey == e.PhysicalKey)
            _lastPhysicalKey = PhysicalKey.None;
    }

    private void OnTextInput(string route, TextInputEventArgs e)
    {
        if (!HasTestFocus()) return;
        Log($"TEXT_INPUT route={route} focused={Target()}" +
            $" source={e.Source?.GetType().Name ?? "<null>"}" +
            $" text={Quote(e.Text)} scalars={Scalars(e.Text)}" +
            $" precedingPhysicalKey={_lastPhysicalKey} Handled={e.Handled}" +
            $" | {X11Snapshot.State()}");
    }

    private static string Quote(string? text)
    {
        if (text is null) return "<null>";
        return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Scalars(string? text)
        => text is null ? "<null>" :
            string.Join(" ", text.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));

    private void LogBlock(string title, string contents)
    {
        Log($"===== {title} =====");
        foreach (string line in contents.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
            if (line.Length != 0) Log(line);
        Log($"===== End {title} =====");
    }

    private void Log(string message)
    {
        string line = $"{DateTimeOffset.UtcNow:O} elapsed_ms={_clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}" +
            $" event={++_serial:000000} {message}";
        _writer.WriteLine(line);
        Console.WriteLine(line);

        _visible.AppendLine(line);
        if (_visible.Length > 120_000)
            _visible.Remove(0, _visible.Length - 80_000);
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            string visibleText = _visible.ToString();
            _logView.Text = visibleText;
            _logView.CaretIndex = visibleText.Length;
        }, DispatcherPriority.Background);
    }
}
