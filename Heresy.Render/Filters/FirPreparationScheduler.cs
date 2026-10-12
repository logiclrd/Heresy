using System;
using System.Collections.Generic;
using System.Threading;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// One off-PCM preparation thread per live audio producer. Each invocation
/// owns a mailbox: only the rendering thread publishes requests/consumes
/// immutable prepared plans. The worker reads only frozen authoring data
/// and snapshots copied at block boundaries, never running DSP histories.
/// </summary>
public sealed class FirPreparationScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<FirPreparationMailbox> _queue = new();
    private readonly Thread _thread;
    private bool _stopped;
    private Exception? _fault;
    private long _completed;

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
    internal void RecordCompleted() => Interlocked.Increment(ref _completed);

    internal void Enqueue(FirPreparationMailbox mailbox)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _queue.Enqueue(mailbox);
            Monitor.Pulse(_gate);
        }
    }

    private void Work()
    {
        while (true)
        {
            FirPreparationMailbox next;
            lock (_gate)
            {
                while (!_stopped && _queue.Count == 0)
                    Monitor.Wait(_gate);
                if (_stopped) return;
                next = _queue.Dequeue();
            }
            try
            {
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
internal sealed class FirPreparationMailbox
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

    internal void Prepare(FirPreparationScheduler scheduler)
    {
        long version;
        int count;
        lock (_gate)
        {
            version = _version;
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

    internal void MarkFailed(FirPreparationScheduler scheduler)
    {
        lock (_gate)
        {
            _ready = null;
            _queued = false;
        }
    }
}
