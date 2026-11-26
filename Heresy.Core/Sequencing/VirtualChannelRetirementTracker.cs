using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Cursor-owned accounting for generated virtual identities that have
/// accepted raw events. A release closes the producer side; the last
/// accepted event's departure closes the scheduler side. The consumer
/// must still defer the notification across its one-event lookahead.
/// No history is retained for closed, drained or unmaterialized IDs.
/// </summary>
internal sealed class VirtualChannelRetirementTracker(
    long owner, Action<long, VirtualChannelIdentity> finalUse)
{
    private readonly record struct Entry(int Pending, bool Closed);
    private readonly Dictionary<VirtualChannelIdentity, Entry> _entries = [];

    public void Accept(VirtualChannelIdentity id)
    {
        if (id.Generation == 0) return;
        _entries.TryGetValue(id, out Entry entry);
        if (entry.Closed)
            throw new InvalidOperationException("Virtual channel received an event after producer release.");
        _entries[id] = entry with { Pending = checked(entry.Pending + 1) };
    }

    public void Depart(VirtualChannelIdentity id)
    {
        if (id.Generation == 0) return;
        if (!_entries.TryGetValue(id, out Entry entry) || entry.Pending == 0)
            throw new InvalidOperationException("Virtual channel event accounting underflow.");
        Update(id, entry with { Pending = entry.Pending - 1 });
    }

    public void Release(VirtualChannelIdentity id)
    {
        if (id.Generation == 0)
            throw new ArgumentException("Legacy virtual channels cannot be released.", nameof(id));
        if (_entries.TryGetValue(id, out Entry entry))
            Update(id, entry with { Closed = true });
    }

    public void CloseProducer()
    {
        foreach (VirtualChannelIdentity id in _entries.Keys.ToArray())
            Release(id);
    }

    public void Abandon()
    {
        // No further delivery is possible. This includes cursor-local
        // buffers and wall deadlines removed by the owning timeline.
        foreach (VirtualChannelIdentity id in _entries.Keys.ToArray())
        {
            _entries.Remove(id);
            finalUse(owner, id);
        }
    }

    private void Update(VirtualChannelIdentity id, Entry entry)
    {
        if (entry.Closed && entry.Pending == 0)
        {
            _entries.Remove(id);
            finalUse(owner, id);
        }
        else
            _entries[id] = entry;
    }
}
