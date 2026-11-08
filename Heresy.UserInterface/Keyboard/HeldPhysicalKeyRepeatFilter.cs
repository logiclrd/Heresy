using System;
using System.Collections.Generic;

using Avalonia.Input;
using Avalonia.Threading;

namespace Heresy.UserInterface.Keyboard;

/// <summary>
/// X11 without detectable autorepeat (including some Xephyr sessions) emits
/// KeyUp/KeyDown pairs for each repeat. Distinguish those pairs from a real
/// release using a short, per-physical-key grace period. Ordinary repeated
/// KeyDown events are suppressed by the same held-key set.
///
/// This model is independent of the UI clock for deterministic tests.
/// Call CollectReleased after the grace interval; a repeated KeyDown before
/// that point cancels the pending release without starting a new press.
/// </summary>
public sealed class HeldPhysicalKeyRepeatState
{
    public const int ReleaseGraceMilliseconds = 40;

    private readonly HashSet<PhysicalKey> _held = [];
    private readonly Dictionary<PhysicalKey, long> _pendingReleaseAt = [];

    public bool IsHeld(PhysicalKey key) => _held.Contains(key);
    public int HeldCount => _held.Count;
    public int PendingReleaseCount => _pendingReleaseAt.Count;

    /// <returns>True for an actual new key press, false for a repeat.</returns>
    public bool KeyDown(PhysicalKey key)
    {
        _pendingReleaseAt.Remove(key);
        return _held.Add(key);
    }

    /// <returns>True if the key was previously held and its release
    /// has been scheduled. This does not immediately clear held state.</returns>
    public bool KeyUp(PhysicalKey key, long nowMilliseconds)
    {
        if (!_held.Contains(key))
            return false;
        // Repeated KeyUp without a KeyDown must not indefinitely postpone
        // a legitimate release.
        _pendingReleaseAt.TryAdd(key, nowMilliseconds);
        return true;
    }

    public IReadOnlyList<PhysicalKey> CollectReleased(long nowMilliseconds)
    {
        if (_pendingReleaseAt.Count == 0)
            return Array.Empty<PhysicalKey>();

        List<PhysicalKey>? released = null;
        foreach ((PhysicalKey key, long began) in _pendingReleaseAt)
            if (nowMilliseconds - began >= ReleaseGraceMilliseconds)
                (released ??= []).Add(key);

        if (released is null)
            return Array.Empty<PhysicalKey>();

        foreach (PhysicalKey key in released)
        {
            _pendingReleaseAt.Remove(key);
            _held.Remove(key);
        }
        return released;
    }

    /// <summary>For focus loss, window deactivation or control disposal:
    /// discard all pending events. The owner separately handles any immediate
    /// note-release actions, so stale timer callbacks cannot end new notes.</summary>
    public void Clear()
    {
        _pendingReleaseAt.Clear();
        _held.Clear();
    }
}

/// <summary>
/// UI-thread adapter that issues confirmed releases only after the X11
/// synthetic KeyUp/KeyDown pairing window. The timer runs only while at
/// least one key release is pending. This intentionally does not suppress
/// key repeat in general text inputs, tracker editing or navigation.
/// </summary>
public sealed class HeldPhysicalKeyRepeatFilter
{
    private readonly HeldPhysicalKeyRepeatState _state = new();
    private readonly Action<PhysicalKey> _onReleased;
    private readonly DispatcherTimer _timer;

    public HeldPhysicalKeyRepeatFilter(Action<PhysicalKey> onReleased)
    {
        _onReleased = onReleased ?? throw new ArgumentNullException(nameof(onReleased));
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        _timer.Tick += OnTimerTick;
    }

    public bool IsHeld(PhysicalKey key) => _state.IsHeld(key);

    public bool KeyDown(PhysicalKey key)
    {
        DrainExpired();
        bool fresh = _state.KeyDown(key);
        if (_state.PendingReleaseCount == 0)
            _timer.Stop();
        return fresh;
    }

    public bool KeyUp(PhysicalKey key)
    {
        DrainExpired();
        if (!_state.KeyUp(key, Environment.TickCount64))
            return false;
        _timer.Start();
        return true;
    }

    public void Clear()
    {
        _timer.Stop();
        _state.Clear();
    }

    private void OnTimerTick(object? sender, EventArgs e) => DrainExpired();

    private void DrainExpired()
    {
        IReadOnlyList<PhysicalKey> released =
            _state.CollectReleased(Environment.TickCount64);
        if (_state.PendingReleaseCount == 0)
            _timer.Stop();
        foreach (PhysicalKey key in released)
            _onReleased(key);
    }
}
