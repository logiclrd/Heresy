using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternPunctuationCommand
{
    ToggleEditMask,
    ClearField,
}

/// <summary>
/// Physical tracker shortcuts at the US-QWERTY comma/period positions,
/// immediately above the right Alt key. These are as layout-independent
/// as the tracker piano. Dvorak/AZERTY text symbols do not participate.
///
/// The caller uses this only for Note, Source, and Volume fields.
/// The Effect command/parameter column deliberately continues to process
/// the active layout's text input, including '.' and effect letters.
/// </summary>
public static class PatternPunctuationKeyboard
{
    public static bool TryGetCommand(
        PhysicalKey physicalKey,
        KeyModifiers modifiers,
        out PatternPunctuationCommand command)
    {
        command = default;
        if (modifiers != KeyModifiers.None)
            return false;

        switch (physicalKey)
        {
            case PhysicalKey.Comma:
                command = PatternPunctuationCommand.ToggleEditMask;
                return true;
            case PhysicalKey.Period:
                command = PatternPunctuationCommand.ClearField;
                return true;
            default:
                return false;
        }
    }
}
