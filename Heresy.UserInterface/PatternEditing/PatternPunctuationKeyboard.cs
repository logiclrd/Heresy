using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternPunctuationCommand
{
    ToggleEditMask,
    ClearCell,
}

/// <summary>
/// Interpret tracker punctuation by the glyph the *current layout* produces,
/// while retaining a physical-key fallback when a platform omits KeySymbol.
/// The piano remains intentionally driven by PhysicalKey. In particular,
/// QWERTY W/E are piano positions, but Dvorak W/E produce ','/'.' and
/// must be dispatched here before piano notes are considered.
/// </summary>
public static class PatternPunctuationKeyboard
{
    public static bool TryGetCommand(
        PhysicalKey physicalKey,
        Key key,
        string? keySymbol,
        KeyModifiers modifiers,
        out PatternPunctuationCommand command)
    {
        command = default;
        if (modifiers != KeyModifiers.None
            || physicalKey == PhysicalKey.NumPadDecimal)
            return false;

        // KeySymbol is the authoritative unshifted character produced
        // on this layout. A populated non-punctuation symbol prevents a
        // physical QWERTY-position fallback from stealing piano keys.
        if (!string.IsNullOrEmpty(keySymbol))
        {
            if (keySymbol == ",")
            {
                command = PatternPunctuationCommand.ToggleEditMask;
                return true;
            }
            if (keySymbol == ".")
            {
                command = PatternPunctuationCommand.ClearCell;
                return true;
            }
            return false;
        }

        // Some synthetic / platform events do not supply KeySymbol.
        // First try their translated logical Key, then conventional
        // punctuation positions; never treat physical W/E alone as a
        // punctuation command, since both are physical piano notes.
        if (key == Key.OemComma || physicalKey == PhysicalKey.Comma)
        {
            command = PatternPunctuationCommand.ToggleEditMask;
            return true;
        }

        if (key == Key.OemPeriod || physicalKey == PhysicalKey.Period)
        {
            command = PatternPunctuationCommand.ClearCell;
            return true;
        }

        return false;
    }
}
