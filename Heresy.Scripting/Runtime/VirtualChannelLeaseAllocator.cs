using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Runtime;

/// <summary>
/// Invocation-local, single-owner allocator. Slot storage tracks peak
/// simultaneous leases, not the total number allocated over time.
/// A released identity remains a valid immutable scheduled target, but
/// cannot release or mutate ownership of the next lease on that slot.
/// </summary>
internal sealed class VirtualChannelLeaseAllocator
{
    private readonly Stack<uint> _available = new();
    private readonly Dictionary<uint, ulong> _owned = new();
    private ulong _nextSlot;
    private ulong _generation;

    internal int ActiveCount => _owned.Count;
    internal int ReusableCount => _available.Count;
    internal ulong SlotCount => _nextSlot;

    public VirtualChannelIdentity Acquire()
    {
        // Check exhaustion before changing either free or owned storage.
        if (_generation == ulong.MaxValue)
            throw new OverflowException("Virtual channel generations are exhausted.");
        if (_available.Count == 0 && _nextSlot > uint.MaxValue)
            throw new OverflowException("Virtual channel slots are exhausted.");

        uint slot = _available.Count != 0
            ? _available.Pop()
            : (uint)_nextSlot++;
        ulong generation = ++_generation;
        _owned.Add(slot, generation);
        return new VirtualChannelIdentity(slot, generation);
    }

    public bool Owns(VirtualChannelIdentity identity)
        => identity.Generation != 0
            && _owned.TryGetValue(identity.Slot, out ulong generation)
            && generation == identity.Generation;

    internal void ReleaseAll()
    {
        foreach (uint slot in _owned.Keys)
            _available.Push(slot);
        _owned.Clear();
    }

    public bool Release(VirtualChannelIdentity identity)
    {
        if (!Owns(identity))
            return false;
        _owned.Remove(identity.Slot);
        _available.Push(identity.Slot);
        return true;
    }
}
