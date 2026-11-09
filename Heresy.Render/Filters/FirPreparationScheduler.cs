using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>Independent bounded preparation request, served by the one
/// existing non-PCM control worker. An invocation owns its own latest-wins
/// mailbox; no mutable DSP state crosses into this thread.</summary>
internal interface IRealtimePreparationWorkItem
{
    void Prepare(FirPreparationScheduler scheduler);
    void MarkFailed(FirPreparationScheduler scheduler);
}

/// <summary>
/// One off-PCM preparation thread per live audio producer. Each invocation
/// owns a mailbox: only the rendering thread publishes requests/consumes
/// immutable prepared plans. The worker reads only frozen authoring data
/// and snapshots copied at block boundaries, never running DSP histories.
/// </summary>
public sealed class FirPreparationScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<IRealtimePreparationWorkItem> _queue = new();
    private readonly Thread _thread;
    private bool _stopped;
    private Exception? _fault;
    private long _completed;
    // Only Multi expansion admissions are capped here. FIR mailboxes retain
    // their existing bounded, per-stage latest-wins scheduling contract.
    // A refused Multi request stays in its invocation mailbox and retries
    // on a later producer block without blocking the audio thread.
    public const int MaximumQueuedMultiExpansions = 64;
    private int _peakQueuedWorkItems;
    private long _completedMulti;
    private long _cancelledMulti;
    private long _preparedMultiConstituents;
    private long _abandonedMultiConstituents;
    private long _multiPreparationTicks;
    private long _multiPreparationAllocatedBytes;
    private long _abandonedMultiPreparationAllocatedBytes;
    private long _peakMultiPreparationAllocatedBytes;
    internal Action? BeforePrepareWorkItem { get; set; }
    // Deterministic concurrency-test gate on the OFF-PCM worker only.
    internal Action? AfterMultiConstituentPrepared { get; set; }

    public FirPreparationScheduler()
    {
        _thread = new Thread(Work)
        {
            Name = "Heresy FIR control preparation",
            IsBackground = true,
        };
        _thread.Start();
    }

    public Exception? Fault => Volatile.Read(ref _fault);
    public long CompletedPreparationCount => Interlocked.Read(ref _completed);
    public long CompletedMultiExpansionCount => Interlocked.Read(ref _completedMulti);
    public long CancelledMultiExpansionCount => Interlocked.Read(ref _cancelledMulti);
    public long PreparedMultiConstituentCount =>
        Interlocked.Read(ref _preparedMultiConstituents);
    public long AbandonedMultiConstituentCount =>
        Interlocked.Read(ref _abandonedMultiConstituents);
    public long MultiPreparationAllocatedBytes =>
        Interlocked.Read(ref _multiPreparationAllocatedBytes);
    public long AbandonedMultiPreparationAllocatedBytes =>
        Interlocked.Read(ref _abandonedMultiPreparationAllocatedBytes);
    public long PeakMultiPreparationAllocatedBytes =>
        Interlocked.Read(ref _peakMultiPreparationAllocatedBytes);
    public TimeSpan MultiPreparationElapsedTime =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _multiPreparationTicks)
            / Stopwatch.Frequency);
    public int PeakQueuedWorkItems => Volatile.Read(ref _peakQueuedWorkItems);
    internal void RecordCompleted() => Interlocked.Increment(ref _completed);
    internal void RecordCancelledMultiExpansion(
        int abandonedChildren = 0, long abandonedAllocatedBytes = 0)
    {
        Interlocked.Increment(ref _cancelledMulti);
        Interlocked.Add(ref _abandonedMultiConstituents, abandonedChildren);
        Interlocked.Add(ref _abandonedMultiPreparationAllocatedBytes,
            abandonedAllocatedBytes);
        Max(ref _peakMultiPreparationAllocatedBytes, abandonedAllocatedBytes);
    }

    private static void Max(ref long target, long observed)
    {
        long old;
        while ((old = Interlocked.Read(ref target)) < observed
            && Interlocked.CompareExchange(ref target, observed, old) != old)
        {
        }
    }

    private void UpdatePeakQueue()
    {
        int count = _queue.Count;
        int old;
        while ((old = Volatile.Read(ref _peakQueuedWorkItems)) < count
            && Interlocked.CompareExchange(
                ref _peakQueuedWorkItems, count, old) != old)
        {
        }
    }

    internal void RecordMultiExpansion(int childCount,
        long elapsedTicks, long allocatedBytes)
    {
        Interlocked.Increment(ref _completedMulti);
        Interlocked.Add(ref _preparedMultiConstituents, childCount);
        Interlocked.Add(ref _multiPreparationTicks, elapsedTicks);
        Interlocked.Add(ref _multiPreparationAllocatedBytes, allocatedBytes);
        Max(ref _peakMultiPreparationAllocatedBytes, allocatedBytes);
        RecordCompleted();
    }

    internal bool TryEnqueueMultiExpansion(IRealtimePreparationWorkItem mailbox)
    {
        lock (_gate)
        {
            if (_stopped || _queue.Count >= MaximumQueuedMultiExpansions)
                return false;
            _queue.Enqueue(mailbox);
            UpdatePeakQueue();
            Monitor.Pulse(_gate);
            return true;
        }
    }

    internal void Enqueue(IRealtimePreparationWorkItem mailbox)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _queue.Enqueue(mailbox);
            UpdatePeakQueue();
            Monitor.Pulse(_gate);
        }
    }

    private void Work()
    {
        while (true)
        {
            IRealtimePreparationWorkItem next;
            lock (_gate)
            {
                while (!_stopped && _queue.Count == 0)
                    Monitor.Wait(_gate);
                if (_stopped) return;
                next = _queue.Dequeue();
            }
            try
            {
                BeforePrepareWorkItem?.Invoke(); // deterministic test gate
                next.Prepare(this);
            }
            catch (Exception exception)
            {
                // No worker exception may kill the PCM producer or silently
                // stall the queue. A later valid snapshot can recover.
                Volatile.Write(ref _fault, exception);
                next.MarkFailed(this);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
        if (Thread.CurrentThread != _thread)
            _thread.Join();
    }
}

/// <summary>
/// Invocation-private latest-wins mailbox. Request slots are preallocated,
/// so PCM/control boundaries never allocate a copy of a FIR proposal.
/// Version checking prevents a worker result prepared from a superseded
/// snapshot from becoming visible to the rendering thread.
/// </summary>
internal sealed class FirPreparationMailbox : IRealtimePreparationWorkItem
{
    private readonly object _gate = new();
    private readonly FilterChain _authored;
    private readonly FirControlRequest[] _pending =
        new FirControlRequest[FirControlPreparationPolicy.MaximumQueuedStages];
    private readonly FirControlRequest[] _workerCopy =
        new FirControlRequest[FirControlPreparationPolicy.MaximumQueuedStages];
    private readonly FirControlPreparationPolicy _workerPolicy;
    private int _pendingCount;
    private long _version;
    private long _preparingVersion;
    private bool _queued;
    private PreparedFirControlBatch? _ready;
    internal Action? BeforePrepare { get; set; }

    internal FirPreparationMailbox(FilterChain authored, int sampleRate)
    {
        _authored = new FilterChain(authored.Layers);
        _workerPolicy = new FirControlPreparationPolicy(sampleRate);
    }

    /// <summary>PCM block boundary only. No kernel design, heap allocation
    /// or wait for the worker. The current accepted/active DSP is untouched.</summary>
    internal bool CapturePending(FirControlPreparationPolicy producer,
        FirPreparationScheduler scheduler)
    {
        if (producer.PendingCount == 0) return false;
        bool enqueue;
        lock (_gate)
        {
            int count = producer.PendingCount;
            if ((_queued || _ready is not null) && count == _pendingCount)
            {
                bool identical = true;
                for (int i = 0; i < count; i++)
                    if (producer.GetPending(i) != _pending[i])
                    {
                        identical = false;
                        break;
                    }
                if (identical)
                {
                    producer.Clear();
                    return false;
                }
            }
            _pendingCount = producer.CopyPending(_pending);
            producer.Clear();
            _version++;
            _ready = null;
            enqueue = !_queued;
            _queued = true;
        }
        if (enqueue) scheduler.Enqueue(this);
        return true;
    }

    /// <summary>PCM block boundary only; returned kernels were fully
    /// prepared by the other thread. A consumer still checks exact numeric
    /// snapshot identity before committing to its own DSP state.</summary>
    internal PreparedFirControlBatch? TakeReady()
    {
        lock (_gate)
        {
            PreparedFirControlBatch? candidate = _ready;
            _ready = null;
            return candidate;
        }
    }

    public void Prepare(FirPreparationScheduler scheduler)
    {
        long version;
        int count;
        lock (_gate)
        {
            version = _version;
            _preparingVersion = version;
            count = _pendingCount;
            Array.Copy(_pending, _workerCopy, count);
        }

        BeforePrepare?.Invoke(); // optional deterministic concurrency-test gate
        PreparedFirControlBatch? candidate = null;
        if (count > 0
            && _workerPolicy.TryOffer(_workerCopy.AsSpan(0, count)))
            _workerPolicy.TryPreparePending(_authored, out candidate);

        bool retry;
        lock (_gate)
        {
            retry = _version != version;
            if (!retry)
            {
                _ready = candidate;
                _queued = false;
            }
        }
        if (retry)
            scheduler.Enqueue(this);
        else
            scheduler.RecordCompleted();
    }

    public void MarkFailed(FirPreparationScheduler scheduler)
    {
        bool retry;
        lock (_gate)
        {
            _ready = null;
            // A newer request captured while an older worker attempt
            // faulted must not be stranded in a permanently queued state.
            // Never retry a known-failing identical snapshot in a loop.
            retry = _version != _preparingVersion;
            if (!retry) _queued = false;
        }
        if (retry) scheduler.Enqueue(this);
    }
}
