using System;
using System.Runtime.InteropServices;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// A platform query for the *latched* Caps Lock state, not whether its
/// physical key is held. Null means the platform cannot report it.
/// Avalonia KeyModifiers deliberately excludes locking modifiers.
/// </summary>
public interface ICapsLockQuery
{
    bool? IsCapsLockOn();
}

/// <summary>Native X11 XKB state, distinguishing physical Shift from the
/// persistent Lock modifier. Available only with an accessible X11 display.</summary>
public readonly record struct X11KeyboardModifiers(
    bool CapsLocked, bool ShiftActive);

public interface IX11KeyboardModifierQuery
{
    X11KeyboardModifiers? Read();
}

public sealed class PlatformCapsLockQuery : ICapsLockQuery, IX11KeyboardModifierQuery
{
    public bool? IsCapsLockOn()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return (GetKeyState(0x14) & 1) != 0; // VK_CAPITAL

            if (OperatingSystem.IsMacOS())
                return (CGEventSourceFlagsState(0) & (1UL << 16)) != 0;

            if (OperatingSystem.IsLinux())
                return ReadX11CapsLock();

            return null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }

    /// <summary>Read native modifiers separately from Avalonia's translated
    /// KeyModifiers. Some Xephyr keyboard mappings treat the XKB LockMask
    /// as Shift while producing text, even when physical Shift is up.
    /// A latched lock must never be mistaken for a physically held Shift.</summary>
    public X11KeyboardModifiers? Read()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try { return ReadX11Modifiers(); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }

    private static bool? ReadX11CapsLock()
        => ReadX11Modifiers()?.CapsLocked;

    private static X11KeyboardModifiers? ReadX11Modifiers()
    {
        // DISPLAY is the active X server, including Xephyr. Native Wayland
        // has no general-purpose global lock-state query. Without X11, do
        // not attempt to reinterpret OS-supplied TextInput characters.
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("DISPLAY")))
            return null;

        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return null;
        try
        {
            if (XkbGetState(display, 0x0100, out XkbState state) != 0)
                return null;
            // Core X11: ShiftMask = 0x01, LockMask = 0x02.
            // BaseMods is physically depressed, LatchedMods represents
            // one-shot/sticky Shift, and LockedMods is the Caps toggle.
            return new X11KeyboardModifiers(
                (state.LockedMods & 0x02) != 0,
                ((state.BaseMods | state.LatchedMods) & 0x01) != 0);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    // Match XkbStateRec in X11/extensions/XKBstr.h byte-for-byte.
    // The modern X11 layout has two 16-bit group fields and places
    // locked_group BEFORE them. Treating every group field as a byte
    // inadvertently makes LockedMods alias native base_mods (the modifier
    // held down right now), so Caps Lock appears to turn off on key-up.
    [StructLayout(LayoutKind.Sequential)]
    private struct XkbState
    {
        public byte Group;
        public byte LockedGroup;
        public ushort BaseGroup;
        public ushort LatchedGroup;
        public byte Mods;
        public byte BaseMods;
        public byte LatchedMods;
        public byte LockedMods;
        public byte CompatState;
        public byte GrabMods;
        public byte CompatGrabMods;
        public byte LookupMods;
        public byte CompatLookupMods;
        public ushort PointerButtons;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
        EntryPoint = "CGEventSourceFlagsState")]
    private static extern ulong CGEventSourceFlagsState(int stateId);

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", EntryPoint = "XkbGetState")]
    private static extern int XkbGetState(IntPtr display, uint device,
        out XkbState state);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);
}
