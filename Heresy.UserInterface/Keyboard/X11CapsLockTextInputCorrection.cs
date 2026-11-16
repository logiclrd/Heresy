using System;
using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// A small X11/Xephyr compatibility shim at the TopLevel's tunneling
/// KeyDown/TextInput route. It corrects the four observed US-layout Caps
/// Lock punctuation anomalies before the stock TextBox handles TextInput.
/// No TextBox subclass, custom style, theme or control replacement is needed.
/// </summary>
public sealed class X11CapsLockTextInputCorrection : IDisposable
{
    private sealed class PendingKey
    {
        public PhysicalKey Key;
        public bool HasControlAltOrMeta;
    }

    private readonly IX11KeyboardModifierQuery _query;
    private readonly ConditionalWeakTable<TopLevel, PendingKey> _pending = new();
    private readonly IDisposable _keyHandler;
    private readonly IDisposable _textHandler;

    public X11CapsLockTextInputCorrection(
        IX11KeyboardModifierQuery? query = null)
    {
        _query = query ?? new PlatformCapsLockQuery();
        // The ancestor sees the events before a child TextBox/other editor
        // handles them; after TextInput's tunnel the stock control performs
        // its own selection, caret, validation, and undo processing.
        _keyHandler = InputElement.KeyDownEvent.AddClassHandler<TopLevel>(
            OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _textHandler = InputElement.TextInputEvent.AddClassHandler<TopLevel>(
            OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnKeyDown(TopLevel host, KeyEventArgs e)
    {
        PendingKey pending = _pending.GetValue(host, _ => new PendingKey());
        pending.Key = e.PhysicalKey;
        pending.HasControlAltOrMeta = (e.KeyModifiers &
            (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        // KeyModifiers.Shift is deliberately not consulted: the reported
        // Xephyr bug itself may misrepresent Caps Lock as Shift.
    }

    private void OnTextInput(TopLevel host, TextInputEventArgs e)
    {
        PendingKey pending = _pending.GetValue(host, _ => new PendingKey());
        PhysicalKey key = pending.Key;
        bool hasOtherModifier = pending.HasControlAltOrMeta;
        pending.Key = PhysicalKey.None;
        pending.HasControlAltOrMeta = false;

        if (!hasOtherModifier && e.Text is string text
            && LooksLikeAffectedPunctuation(key, text))
            e.Text = Correct(key, text, _query.Read());
    }

    private static bool LooksLikeAffectedPunctuation(PhysicalKey key, string text)
        => (key, text) is
            (PhysicalKey.Quote, "\"") or
            (PhysicalKey.Comma, "<") or
            (PhysicalKey.Period, ">") or
            (PhysicalKey.Semicolon, ":");

    /// <summary>Pure, narrow US keymap correction. Actual Shift (including
    /// XKB latched/sticky Shift), ordinary Caps-off, null native state, and
    /// foreign/IME/composed text are intentionally left unchanged.</summary>
    public static string Correct(
        PhysicalKey key, string text, X11KeyboardModifiers? modifiers)
    {
        if (modifiers is not { CapsLocked: true, ShiftActive: false })
            return text;
        return (key, text) switch
        {
            (PhysicalKey.Quote, "\"") => "'",
            (PhysicalKey.Comma, "<") => ",",
            (PhysicalKey.Period, ">") => ".",
            (PhysicalKey.Semicolon, ":") => ";",
            _ => text,
        };
    }

    public void Dispose()
    {
        _keyHandler.Dispose();
        _textHandler.Dispose();
    }
}
