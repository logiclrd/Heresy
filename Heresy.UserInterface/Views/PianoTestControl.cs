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
/// Common Test area for Synth and Sample editors. This control owns Test
/// piano and octave gestures plus window-wide Caps Lock and F8 observation.
/// Its consumers receive only musical preview events, not keyboard state.
/// </summary>
public sealed class PianoTestControl : UserControl
{
    private readonly PianoPreviewKeyboard _keyboard = new();
    private readonly HeldPhysicalKeyRepeatFilter _repeats;
    private readonly HeldPhysicalKeyRepeatFilter _capsRepeats;
    private readonly PianoPreviewCapsLockMode _capsMode;
    private readonly Func<PianoPreviewEvent, Task> _onEvent;
    private readonly Func<bool> _hasVoices;
    private readonly Func<Task> _stopOwnVoices;
    private readonly Border _testArea;
    private readonly TextBlock _octave = new();
    private readonly TextBlock _mode = new();
    private Window? _owner;
    public PianoTestControl(
        Func<PianoPreviewEvent, Task> onEvent,
        Func<bool> hasVoices,
        Func<Task> stopOwnVoices,
        ICapsLockQuery? capsLockQuery = null)
    {
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _hasVoices = hasVoices ?? throw new ArgumentNullException(nameof(hasVoices));
        _stopOwnVoices = stopOwnVoices ?? throw new ArgumentNullException(nameof(stopOwnVoices));
        _capsMode = new PianoPreviewCapsLockMode(
            capsLockQuery ?? new PlatformCapsLockQuery());
        _repeats = new HeldPhysicalKeyRepeatFilter(
            key => DispatchConfirmedRelease(key));
        // Caps Lock is observed even when Test is not focused; do not clear
        // its repeat history when the Test area loses focus.
        _capsRepeats = new HeldPhysicalKeyRepeatFilter(_ => { });

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

    private void UpdateLabels()
    {
        _octave.Text = $"Audition octave: {_keyboard.BaseOctave}";
        _mode.Text = $"Mode: {(_capsMode.IsLatch ? "Latch" : "Hold")}"
            + (_capsMode.IsNativeStateAvailable ? "" : " (locally tracked)");
    }

    private void OnTestKeyDown(object? sender, KeyEventArgs e)
    {
        if (!PianoPreviewKeyboard.IsApplicable(e.PhysicalKey, e.KeyModifiers))
            return;
        e.Handled = true;
        if (!_repeats.KeyDown(e.PhysicalKey))
            return;
        // Refresh authoritative OS state for each newly started note.
        // When unavailable, Refresh preserves the in-application mode.
        _capsMode.Refresh();
        PianoPreviewEvent? preview = _keyboard.KeyDown(
            e.PhysicalKey, e.KeyModifiers, _capsMode.IsLatch);
        if (preview is not null)
            Emit(preview);
        UpdateLabels();
    }

    private void OnTestKeyUp(object? sender, KeyEventArgs e)
    {
        if (_repeats.KeyUp(e.PhysicalKey))
            e.Handled = true;
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

    private void OnOwnerActivated(object? sender, EventArgs e)
    {
        // Resynchronize if the user toggled Caps Lock while another window
        // was active. An unavailable query leaves the local mode unchanged.
        _capsMode.Refresh();
        UpdateLabels();
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        ReleaseHeldKeys();
        _capsRepeats.Clear();
    }

    private void OnOwnerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.CapsLock)
        {
            // Tunnel from the containing window, even for controls that
            // handle their own keyboard events. We observe without handling.
            if (_capsRepeats.KeyDown(e.PhysicalKey))
            {
                _capsMode.OnCapsLockPressed();
                UpdateLabels();
            }
            return;
        }

        if (e.Key != Key.F8 || (!_hasVoices() && !_testArea.IsFocused))
            return;
        e.Handled = true;
        if (_repeats.KeyDown(e.PhysicalKey))
            Emit(new PianoPreviewStopAll());
    }

    private void OnOwnerKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.CapsLock)
        {
            _capsRepeats.KeyUp(e.PhysicalKey);
            // Some platform queries are updated after KeyDown; read again
            // after the actual toggle key has been released.
            if (_capsMode.IsNativeStateAvailable)
            {
                _capsMode.Refresh();
                UpdateLabels();
            }
            return;
        }

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
            _owner.Activated += OnOwnerActivated;
            _owner.Deactivated += OnOwnerDeactivated;
            _owner.AddHandler(InputElement.KeyDownEvent,
                OnOwnerKeyDown, RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _owner.AddHandler(InputElement.KeyUpEvent,
                OnOwnerKeyUp, RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
        _capsMode.Refresh();
        UpdateLabels();
    }

    protected override void OnDetachedFromVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        if (_owner is not null)
        {
            _owner.Activated -= OnOwnerActivated;
            _owner.Deactivated -= OnOwnerDeactivated;
            _owner.RemoveHandler(InputElement.KeyDownEvent, OnOwnerKeyDown);
            _owner.RemoveHandler(InputElement.KeyUpEvent, OnOwnerKeyUp);
            _owner = null;
        }
        ReleaseHeldKeys();
        _capsRepeats.Clear();
        _ = _stopOwnVoices();
        base.OnDetachedFromVisualTree(e);
    }
}
