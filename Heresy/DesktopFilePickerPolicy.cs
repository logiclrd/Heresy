using System;

using Avalonia;

namespace Heresy;

/// <summary>
/// Selects the X11 file-picker backend without changing Windows or native
/// Wayland file picking. In Avalonia 12.1.5 the GTK path explicitly marks
/// native dialogs as transient for their X11 owner window, unlike relying
/// solely on the separate desktop portal's placement/focus handling.
/// </summary>
public static class DesktopFilePickerPolicy
{
    /// <summary>Set to "portal" to restore Avalonia's default X11 portal
    /// backend for comparison. Other values, including unset, select GTK.</summary>
    public const string EnvironmentVariable = "HERESY_X11_FILE_PICKER";

    public static X11PlatformOptions X11Options(string? preference)
        => new()
        {
            UseDBusFilePicker = string.Equals(preference?.Trim(), "portal",
                StringComparison.OrdinalIgnoreCase),
        };
}
