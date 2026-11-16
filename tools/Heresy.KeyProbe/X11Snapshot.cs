using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Heresy.KeyProbe;

/// <summary>
/// Diagnostic Xlib/XKB observations on a SECOND, read-only X connection.
/// This does not consume or modify Avalonia's event queue, grabs, layout,
/// or XKB state. No X11 event keycode is exposed by Avalonia KeyEventArgs;
/// the map below is a separate snapshot, not an assertion about which
/// X keycode produced any individual Avalonia event.
/// </summary>
internal static class X11Snapshot
{
    private const string LibX11 = "libX11.so.6";
    private const uint XkbUseCoreKbd = 0x0100;
    private static readonly string[] ModifierNames =
        ["Shift", "Lock", "Control", "Mod1", "Mod2", "Mod3", "Mod4", "Mod5"];

    public static string State()
    {
        if (!OperatingSystem.IsLinux())
            return "XKB=n/a(non-Linux)";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            return "XKB=n/a(no DISPLAY)";
        try
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return "XKB=n/a(XOpenDisplay failed)";
            try { return ReadState(display); }
            finally { XCloseDisplay(display); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            return $"XKB=n/a({ex.GetType().Name}: {ex.Message})";
        }
    }

    public static string FullSnapshot()
    {
        StringBuilder b = new();
        b.AppendLine($"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "<unset>"}");
        b.AppendLine($"XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "<unset>"}");
        b.AppendLine($"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "<unset>"}");
        b.AppendLine($"XKB_DEFAULT_LAYOUT={Environment.GetEnvironmentVariable("XKB_DEFAULT_LAYOUT") ?? "<unset>"}");
        b.AppendLine($"XKB_DEFAULT_VARIANT={Environment.GetEnvironmentVariable("XKB_DEFAULT_VARIANT") ?? "<unset>"}");
        b.AppendLine($"XKB_DEFAULT_OPTIONS={Environment.GetEnvironmentVariable("XKB_DEFAULT_OPTIONS") ?? "<unset>"}");
        b.AppendLine($"LANG={Environment.GetEnvironmentVariable("LANG") ?? "<unset>"}");
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("DISPLAY")))
        {
            b.AppendLine("Native X11 snapshot unavailable (not Linux or no DISPLAY).");
            return b.ToString();
        }
        try
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                b.AppendLine("XOpenDisplay failed.");
                return b.ToString();
            }
            try
            {
                b.AppendLine($"X11 vendor={Marshal.PtrToStringAnsi(XServerVendor(display)) ?? "<unknown>"}; release={XVendorRelease(display)}");
                int major = 1, minor = 0;
                if (XkbQueryExtension(display, out int opcode, out int evBase,
                    out int errBase, ref major, ref minor) != 0)
                    b.AppendLine($"XKB extension version={major}.{minor}, opcode={opcode}, eventBase={evBase}, errorBase={errBase}");
                else
                    b.AppendLine("XKB extension unavailable.");
                b.AppendLine(ReadState(display));

                XDisplayKeycodes(display, out int min, out int max);
                b.AppendLine($"Native X11 keycode bounds: {min}..{max}");
                b.AppendLine("Native modifier map (keycodes; group0 unshifted keysym):");
                IntPtr modMap = XGetModifierMapping(display);
                if (modMap == IntPtr.Zero)
                    b.AppendLine("  XGetModifierMapping unavailable");
                else
                {
                    try
                    {
                        int per = Marshal.ReadInt32(modMap);
                        IntPtr mapData = Marshal.ReadIntPtr(modMap, IntPtr.Size);
                        if (per is > 0 and <= 64 && mapData != IntPtr.Zero)
                        {
                            for (int modifier = 0; modifier < 8; modifier++)
                            {
                                b.Append($"  {ModifierNames[modifier]}:");
                                for (int j = 0; j < per; j++)
                                {
                                    int code = Marshal.ReadByte(mapData, modifier * per + j);
                                    if (code != 0)
                                        b.Append($" {code}({Symbol(display, (byte)code, 0, 0)})");
                                }
                                b.AppendLine();
                            }
                        }
                        else b.AppendLine($"  Unexpected max_keypermod={per}");
                    }
                    finally { XFreeModifiermap(modMap); }
                }

                b.AppendLine("XKB keycode map: code: group0/level0 group0/level1 group1/level0 group1/level1");
                for (int code = Math.Max(0, min); code <= Math.Min(255, max); code++)
                {
                    string s0 = Symbol(display, (byte)code, 0, 0);
                    string s1 = Symbol(display, (byte)code, 0, 1);
                    string s2 = Symbol(display, (byte)code, 1, 0);
                    string s3 = Symbol(display, (byte)code, 1, 1);
                    if (s0 is "NoSymbol" && s1 is "NoSymbol" && s2 is "NoSymbol" && s3 is "NoSymbol")
                        continue;
                    b.AppendLine($"  {code,3}: {s0,-18} {s1,-18} {s2,-18} {s3}");
                    if (s0 is "apostrophe" or "quoteright" or "comma" or "period" or "semicolon" ||
                        s1 is "quotedbl" or "less" or "greater" or "colon")
                    {
                        b.Append("       XkbLookupKeySym");
                        foreach (uint mask in new uint[] { 0, 1, 2, 3 })
                        {
                            if (XkbLookupKeySym(display, (byte)code, mask,
                                out uint consumed, out nuint keysym) != 0)
                                b.Append($" mask=0x{mask:x}=>{SymbolName(keysym)}(consumed=0x{consumed:x})");
                            else
                                b.Append($" mask=0x{mask:x}=><failure>");
                        }
                        b.AppendLine();
                    }
                }
                b.AppendLine("Note: keycode map is a snapshot, not native keycodes for individual Avalonia events.");
            }
            finally { XCloseDisplay(display); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            b.AppendLine($"Native X11 query unavailable: {ex}");
        }
        return b.ToString();
    }

    private static string ReadState(IntPtr display)
    {
        if (XkbGetState(display, XkbUseCoreKbd, out XkbState s) != 0)
            return "XkbGetState failed";
        return $"XKB group={s.Group}, lockedGroup={s.LockedGroup}, baseGroup={s.BaseGroup}, latchedGroup={s.LatchedGroup}" +
            $" mods=0x{s.Mods:x2} [{Mask(s.Mods)}]" +
            $" baseMods=0x{s.BaseMods:x2} [{Mask(s.BaseMods)}]" +
            $" latchedMods=0x{s.LatchedMods:x2} [{Mask(s.LatchedMods)}]" +
            $" lockedMods=0x{s.LockedMods:x2} [{Mask(s.LockedMods)}]" +
            $" compatState=0x{s.CompatState:x2}, grabMods=0x{s.GrabMods:x2}," +
            $" compatGrabMods=0x{s.CompatGrabMods:x2}, lookupMods=0x{s.LookupMods:x2}," +
            $" compatLookupMods=0x{s.CompatLookupMods:x2}, pointerButtons=0x{s.PointerButtons:x4}";
    }

    private static string Mask(byte flags)
    {
        StringBuilder b = new();
        for (int i = 0; i < 8; i++)
        {
            if ((flags & (1 << i)) == 0) continue;
            if (b.Length > 0) b.Append('+');
            b.Append(ModifierNames[i]);
        }
        return b.Length == 0 ? "none" : b.ToString();
    }

    private static string Symbol(IntPtr display, byte code, int group, int level)
        => SymbolName(XkbKeycodeToKeysym(display, code, group, level));

    private static string SymbolName(nuint symbol)
    {
        if (symbol == 0) return "NoSymbol";
        IntPtr name = XKeysymToString(symbol);
        return Marshal.PtrToStringAnsi(name) ?? $"0x{symbol:x}";
    }

    // XkbStateRec layout matches X11/extensions/XKBstr.h. Keep the two
    // group fields as 16-bit values: otherwise LockedMods reads BaseMods.
    [StructLayout(LayoutKind.Sequential)]
    private struct XkbState
    {
        public byte Group, LockedGroup;
        public ushort BaseGroup, LatchedGroup;
        public byte Mods, BaseMods, LatchedMods, LockedMods;
        public byte CompatState, GrabMods, CompatGrabMods, LookupMods, CompatLookupMods;
        public ushort PointerButtons;
    }

    [DllImport(LibX11, EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport(LibX11, EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11, EntryPoint = "XServerVendor")]
    private static extern IntPtr XServerVendor(IntPtr display);
    [DllImport(LibX11, EntryPoint = "XVendorRelease")]
    private static extern int XVendorRelease(IntPtr display);
    [DllImport(LibX11, EntryPoint = "XDisplayKeycodes")]
    private static extern int XDisplayKeycodes(IntPtr display, out int min, out int max);
    [DllImport(LibX11, EntryPoint = "XkbQueryExtension")]
    private static extern int XkbQueryExtension(IntPtr display,
        out int opcode, out int eventBase, out int errorBase, ref int major, ref int minor);
    [DllImport(LibX11, EntryPoint = "XkbGetState")]
    private static extern int XkbGetState(IntPtr display, uint device, out XkbState state);
    [DllImport(LibX11, EntryPoint = "XkbKeycodeToKeysym")]
    private static extern nuint XkbKeycodeToKeysym(IntPtr display, byte code, int group, int level);
    [DllImport(LibX11, EntryPoint = "XkbLookupKeySym")]
    private static extern int XkbLookupKeySym(IntPtr display, byte code,
        uint modifiers, out uint modifiersConsumed, out nuint symbol);
    [DllImport(LibX11, EntryPoint = "XKeysymToString")]
    private static extern IntPtr XKeysymToString(nuint keysym);
    [DllImport(LibX11, EntryPoint = "XGetModifierMapping")]
    private static extern IntPtr XGetModifierMapping(IntPtr display);
    [DllImport(LibX11, EntryPoint = "XFreeModifiermap")]
    private static extern int XFreeModifiermap(IntPtr map);
}
