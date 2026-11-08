using System;
using System.Reflection;
using System.Runtime.InteropServices;

using AwesomeAssertions;

using Heresy.UserInterface.Keyboard;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// An ABI regression test, independent of an X11 display. Offsets follow
/// the public X11/extensions/XKBstr.h XkbStateRec layout, NOT the older
/// simplified field listing in some XKB extension documentation.
/// </summary>
[TestFixture]
public sealed class PlatformCapsLockQueryTests
{
    [Test]
    public void XkbStateRecMatchesNativeHeaderFieldOffsetsAndSize()
    {
        Type state = typeof(PlatformCapsLockQuery).GetNestedType(
            "XkbState", BindingFlags.NonPublic)!;
        state.Should().NotBeNull();

        Offset(state, "Group").Should().Be(0);
        Offset(state, "LockedGroup").Should().Be(1);
        Offset(state, "BaseGroup").Should().Be(2);
        Offset(state, "LatchedGroup").Should().Be(4);
        Offset(state, "Mods").Should().Be(6);
        Offset(state, "BaseMods").Should().Be(7);
        Offset(state, "LatchedMods").Should().Be(8);
        Offset(state, "LockedMods").Should().Be(9);
        Offset(state, "CompatState").Should().Be(10);
        Offset(state, "GrabMods").Should().Be(11);
        Offset(state, "CompatGrabMods").Should().Be(12);
        Offset(state, "LookupMods").Should().Be(13);
        Offset(state, "CompatLookupMods").Should().Be(14);
        Offset(state, "PointerButtons").Should().Be(16);
        Marshal.SizeOf(state).Should().Be(18);
    }

    [Test]
    public void XkbPhysicalModifierAndLockedModifierReadDifferentBytes()
    {
        Type state = typeof(PlatformCapsLockQuery).GetNestedType(
            "XkbState", BindingFlags.NonPublic)!;

        // Native XKB: base_mods is at offset 7 (physically depressed),
        // whereas locked_mods is at offset 9 (persistent toggle state).
        byte[] nativeBytes = new byte[Marshal.SizeOf(state)];
        GCHandle handle = GCHandle.Alloc(nativeBytes, GCHandleType.Pinned);
        try
        {
            nativeBytes[7] = 0x02; // LockMask physically down, not locked
            nativeBytes[9] = 0;
            object physicallyHeld = Marshal.PtrToStructure(
                handle.AddrOfPinnedObject(), state)!;
            Read(physicallyHeld, "BaseMods").Should().Be(0x02);
            Read(physicallyHeld, "LockedMods").Should().Be(0);

            nativeBytes[7] = 0;    // Key released
            nativeBytes[9] = 0x02; // LockMask remains locked
            object latchedOn = Marshal.PtrToStructure(
                handle.AddrOfPinnedObject(), state)!;
            Read(latchedOn, "BaseMods").Should().Be(0);
            Read(latchedOn, "LockedMods").Should().Be(0x02);
        }
        finally
        {
            handle.Free();
        }
    }

    private static int Offset(Type native, string field)
        => Marshal.OffsetOf(native, field).ToInt32();

    private static byte Read(object boxedState, string field)
        => (byte)boxedState.GetType().GetField(
            field, BindingFlags.Instance | BindingFlags.Public)!.GetValue(boxedState)!;
}
