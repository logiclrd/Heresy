using System;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// The preview's actual Hold/Latch mode, distinct from physical key state.
/// An available native lock-state query is authoritative. When a platform
/// cannot report it, Caps Lock is simply an in-application mode toggle:
/// start in Hold and retain the last observed mode across focus changes.
/// </summary>
public sealed class PianoPreviewCapsLockMode
{
    private readonly ICapsLockQuery _query;

    public bool IsLatch { get; private set; }
    public bool IsNativeStateAvailable { get; private set; }

    public PianoPreviewCapsLockMode(ICapsLockQuery query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        Refresh();
    }

    /// <summary>Synchronize only when the platform can answer. An unknown
    /// answer must never reset a locally established preview mode.</summary>
    public void Refresh()
    {
        bool? state = _query.IsCapsLockOn();
        IsNativeStateAvailable = state.HasValue;
        if (state.HasValue)
            IsLatch = state.Value;
    }

    /// <summary>Called on a fresh Caps Lock KeyDown observed at window level.
    /// Never called for synthetic held-key repeats. On platforms without a
    /// query, the key toggles Heresy's own mode unconditionally.</summary>
    public void OnCapsLockPressed()
    {
        bool? native = _query.IsCapsLockOn();
        IsNativeStateAvailable = native.HasValue;
        IsLatch = native ?? !IsLatch;
    }
}
