using System;
using System.Threading;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Shared root-render lifetime signal. RequestStop is safe from the host
/// thread; the PCM producer remains the sole owner of all cursor state.
/// An export token can also cancel without registering callbacks.
/// </summary>
public sealed class SequencingCancellation(CancellationToken external = default)
{
    private int _stopRequested;

    public bool IsCancellationRequested =>
        Volatile.Read(ref _stopRequested) != 0 || external.IsCancellationRequested;

    public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);

    public void ThrowIfCancellationRequested()
    {
        external.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopRequested) != 0)
            throw new OperationCanceledException("Sequencing was stopped.");
    }
}
