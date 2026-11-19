using System;

using Avalonia;

namespace Heresy;

/// <summary>
/// Selects the X11 native file picker, or (on Linux, by explicit opt-in)
/// Avalonia's owned managed file chooser. Windows and other platforms keep
/// their normal desktop picker unless an explicitly supported override
/// is requested.
/// </summary>
public static class DesktopFilePickerPolicy
{
    /// <summary>
    /// "gtk" (default): owner-transient GTK X11 native dialogs.
    /// "portal": use KDE/other desktop D-Bus portal dialogs on X11.
    /// "managed": use Avalonia's owned file chooser on Linux.
    /// </summary>
    public const string EnvironmentVariable = "HERESY_X11_FILE_PICKER";

    public static X11PlatformOptions X11Options(string? preference)
        => new()
        {
            UseDBusFilePicker = string.Equals(preference?.Trim(), "portal",
                StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>
    /// The Avalonia-managed provider overrides native platform pickers, so
    /// register it only on Linux when the user explicitly requests it.
    /// Unset keeps the already-tested owner-transient GTK picker on X11.
    /// </summary>
    public static bool UseManagedDialogs(string? preference, bool isLinux)
        => isLinux && string.Equals(preference?.Trim(), "managed",
            StringComparison.OrdinalIgnoreCase);
}
