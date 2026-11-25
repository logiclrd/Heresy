using System;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Immutable virtual channel identity within a playback invocation.
/// Generation zero represents existing numeric engine/preview targets.
/// A recycled script slot always receives a fresh nonzero generation.
/// </summary>
public readonly record struct VirtualChannelIdentity(uint Slot, ulong Generation)
    : IComparable<VirtualChannelIdentity>
{
    public static VirtualChannelIdentity Legacy(uint id) => new(id, 0);

    public int CompareTo(VirtualChannelIdentity other)
    {
        int slot = Slot.CompareTo(other.Slot);
        return slot != 0 ? slot : Generation.CompareTo(other.Generation);
    }
}
