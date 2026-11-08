using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.UserInterface.Keyboard;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Common Test area for Synth and Sample editors. This control owns all
/// keyboard, octave, Caps Lock, F8 and Xephyr repeat behavior. Its consumers
/// receive only musical preview events; they never inspect keyboard state.
/// </summary>
public sealed class PianoTestControl : UserControl
{
    private readonly PianoPreviewKeyboard _keyboard = new();
    private readonly HeldPhysicalKeyRepeatFilter _repeats;
    private readonly ICapsLockQuery _caps;
    private readonly Func<PianoPreviewEvent, Task> _onEvent;
    private readonly Func<bool> _hasVoices;
    private readonly Func<Task> _stopOwnVoices;
    private readonly Border _testArea;
    private readonly TextBlock _octave = new();
    private readonly TextBlock _mode = new();
    private Window? _owner;
    // Unknown is meaningful on native Wayland: there is no portable
    // independent compositor lock-state query. Once Caps Lock is pressed
    // inside Test, the fallback becomes known for this focused session.
    private bool? _localCapsLock;

    public PianoTestControl(
        Func<PianoPreviewEvent, Task> onEvent,
        Func<bool> hasVoices,
        Func<Task> stopOwnVoices,
        ICapsLockQuery? capsLockQuery = null)
    {
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _hasVoices = hasVoices ?? throw new ArgumentNullException(nameof(hasVoices));
        _stopOwnVoices = stopOwnVoices ?? throw new ArgumentNullException(nameof(stopOwnVoices));
        _caps = capsLockQuery ?? new PlatformCapsLockQuery();
        _repeats = new HeldPhysicalKeyRepeatFilter(
            key => DispatchConfirmedRelease(key));

        _testArea = new Border
        {
            Focusable = true,
            Background = Brushes.DimGray,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(22, 9),
            Child = new TextBlock
            {
                Text = "Test",
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
            },
        };
        _testArea.PointerPressed += (_, e) =>
        {
            _testArea.Focus();
            e.Handled = true;
        };
        _testArea.KeyDown += OnTestKeyDown;
        _testArea.KeyUp += OnTestKeyUp;
        _testArea.GotFocus += (_, _) =>
        {
            _testArea.BorderBrush = Brushes.LightSkyBlue;
            UpdateLabels();
        };
        _testArea.LostFocus += (_, _) =>
        {
            _testArea.BorderBrush = Brushes.Gray;
            ReleaseHeldKeys();
        };

        StackPanel description = new()
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        description.Children.Add(_octave);
        description.Children.Add(_mode);
        description.Children.Add(new TextBlock
        {
            Text = "Click Test, then use tracker piano keys (Z S X D C ...). "
                + "Keypad * / changes octave. Caps Lock off: hold notes; "
                + "Caps Lock on: latch notes and suppress close neighbours. "
                + "F8 stops all preview voices.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(10, 6, 10, 10),
        };
        row.Children.Add(_testArea);
        row.Children.Add(description);
        Content = row;
        UpdateLabels();
    }

    public int BaseOctave => _keyboard.BaseOctave;

    private bool CapsLockOn()
    {
        bool? native = _caps.IsCapsLockOn();
        if (native.HasValue)
            _localCapsLock = native;
        return native ?? _localCapsLock ?? false;
    }

    private void UpdateLabels()
    {
        _octave.Text = $"Audition octave: {_keyboard.BaseOctave}";
        bool active = CapsLockOn();
        bool known = _caps.IsCapsLockOn().HasValue;
        _mode.Text = !known && !_localCapsLock.HasValue
            ? "Mode: Hold (Caps Lock state unavailable)"
            : $"Mode: {(active ? "Latch" : "Hold")}"
                + (known ? "" : " (locally tracked)");
    }

    private void OnTestKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.CapsLock)
        {
            e.Handled = true;
            if (_repeats.KeyDown(e.PhysicalKey))
            {
                bool? native = _caps.IsCapsLockOn();
                _localCapsLock = native ?? !(_localCapsLock ?? false);
                UpdateLabels();
            }
            return;
        }
        if (!PianoPreviewKeyboard.IsApplicable(e.PhysicalKey, e.KeyModifiers))
            return;
        e.Handled = true;
        if (!_repeats.KeyDown(e.PhysicalKey))
            return;
        PianoPreviewEvent? preview = _keyboard.KeyDown(
            e.PhysicalKey, e.KeyModifiers, CapsLockOn());
        if (preview is not null)
            Emit(preview);
        UpdateLabels();
    }

    private void OnTestKeyUp(object? sender, KeyEventArgs e)
    {
        if (_repeats.KeyUp(e.PhysicalKey))
            e.Handled = true;

        // X11 can dispatch Caps Lock KeyDown before XKB's locked modifier
        // state is observable through our separate query connection.
        // Query again on KeyUp so turning Caps Lock off updates the mode
        // without requiring a subsequent piano key event.
        if (e.PhysicalKey == PhysicalKey.CapsLock)
            UpdateLabels();
    }

    private void DispatchConfirmedRelease(PhysicalKey key)
    {
        PianoPreviewEvent? preview = _keyboard.KeyUp(key);
        if (preview is not null)
            Emit(preview);
    }

    private void Emit(PianoPreviewEvent evt) => _ = _onEvent(evt);

    private void ReleaseHeldKeys()
    {
        _repeats.Clear();
        IReadOnlyList<PianoPreviewEvent> events = _keyboard.ReleaseHeld();
        foreach (PianoPreviewEvent evt in events)
            Emit(evt);
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
        => ReleaseHeldKeys();

    private void OnOwnerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8 || (!_hasVoices() && !_testArea.IsFocused))
            return;
        e.Handled = true;
        if (_repeats.KeyDown(e.PhysicalKey))
            Emit(new PianoPreviewStopAll());
    }

    private void OnOwnerKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8 && _repeats.KeyUp(e.PhysicalKey))
            e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = TopLevel.GetTopLevel(this) as Window;
        if (_owner is not null)
        {
            _owner.Deactivated += OnOwnerDeactivated;
            _owner.AddHandler(InputElement.KeyDownEvent,
                OnOwnerKeyDown, RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _owner.AddHandler(InputElement.KeyUpEvent,
                OnOwnerKeyUp, RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
        UpdateLabels();
    }

    protected override void OnDetachedFromVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        if (_owner is not null)
        {
            _owner.Deactivated -= OnOwnerDeactivated;
            _owner.RemoveHandler(InputElement.KeyDownEvent, OnOwnerKeyDown);
            _owner.RemoveHandler(InputElement.KeyUpEvent, OnOwnerKeyUp);
            _owner = null;
        }
        ReleaseHeldKeys();
        _ = _stopOwnVoices();
        base.OnDetachedFromVisualTree(e);
    }
}
