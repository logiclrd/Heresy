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

public sealed class PlatformCapsLockQuery : ICapsLockQuery
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

    private static bool? ReadX11CapsLock()
    {
        // DISPLAY is the active X server, including Xephyr. Native Wayland
        // has no general-purpose global lock-state query: wl_keyboard passes
        // locked modifiers only to the compositor's focused client. With no
        // X server, the UI uses a clearly marked, locally observed fallback.
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
            // LockMask is the second core X11 modifier bit.
            return (state.LockedMods & 0x02) != 0;
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
