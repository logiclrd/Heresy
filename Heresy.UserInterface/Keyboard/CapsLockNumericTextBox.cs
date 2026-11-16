using System;

using Avalonia.Controls;
using Avalonia.Input;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// Some nested X11/Xephyr keymaps incorrectly treat Caps Lock as Shift for
/// punctuation and number-row keys (typing 0.2 produces )>@). Fix only
/// the known bad US-layout shifted glyph from the corresponding physical
/// key, only while XKB reports Caps locked and NO depressed/latched Shift.
/// Keep TextBox's normal TextInput path for caret movement, undo, selection,
/// IME, paste, and all nonmatching/other-layout characters.
/// </summary>
public sealed class CapsLockNumericTextBox : TextBox
{
    private readonly IX11KeyboardModifierQuery _modifiers;
    private PhysicalKey _lastKey;
    private bool _otherModifier;

    public CapsLockNumericTextBox(IX11KeyboardModifierQuery? modifiers = null)
        => _modifiers = modifiers ?? new PlatformCapsLockQuery();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _lastKey = e.PhysicalKey;
        // Do not reinterpret AltGr, shortcuts, or composed text. Do NOT
        // test KeyModifiers.Shift: the reported bug is precisely that the
        // backend reports the Caps lock modifier as Shift under Xephyr.
        _otherModifier = (e.KeyModifiers
            & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!_otherModifier && e.Text is string text
            && _modifiers.Read() is { CapsLocked: true, ShiftActive: false } state)
        {
            // Only rewrite a one-character match for its corresponding
            // physical key. Never convert legitimate Shift+digit/symbol,
            // arbitrary pasted/IME strings or an unfamiliar key layout.
            e.Text = RestoreUnshifted(_lastKey, text, state);
        }
        _lastKey = PhysicalKey.None;
        _otherModifier = false;
        base.OnTextInput(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        _lastKey = PhysicalKey.None;
        _otherModifier = false;
        base.OnLostFocus(e);
    }

    public static string RestoreUnshifted(
        PhysicalKey key, string text, X11KeyboardModifiers state)
    {
        if (!state.CapsLocked || state.ShiftActive)
            return text;
        return (key, text) switch
        {
            (PhysicalKey.Digit0, ")") => "0",
            (PhysicalKey.Digit1, "!") => "1",
            (PhysicalKey.Digit2, "@") => "2",
            (PhysicalKey.Digit3, "#") => "3",
            (PhysicalKey.Digit4, "$") => "4",
            (PhysicalKey.Digit5, "%") => "5",
            (PhysicalKey.Digit6, "^") => "6",
            (PhysicalKey.Digit7, "&") => "7",
            (PhysicalKey.Digit8, "*") => "8",
            (PhysicalKey.Digit9, "(") => "9",
            (PhysicalKey.Period, ">") => ".",
            (PhysicalKey.Comma, "<") => ",",
            (PhysicalKey.Minus, "_") => "-",
            (PhysicalKey.Equal, "+") => "=",
            (PhysicalKey.Slash, "?") => "/",
            (PhysicalKey.Semicolon, ":") => ";",
            (PhysicalKey.Quote, "\"") => "'",
            (PhysicalKey.BracketLeft, "{") => "[",
            (PhysicalKey.BracketRight, "}") => "]",
            (PhysicalKey.Backslash, "|") => "\\",
            (PhysicalKey.Backquote, "~") => "`",
            _ => text,
        };
    }
}
