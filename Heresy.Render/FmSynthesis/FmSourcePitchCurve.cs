using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.FmSynthesis;

/// <summary>
/// Allocation-free streaming pitch curve controlled by a Synth source node.
/// Each source frame is evaluated in ascending order. PitchTrajectory integrates
/// the previous frame's multiplier before reading the current one, so retaining
/// two adjacent values is sufficient even for SampleSound's sub-frame position.
/// This is deliberately not a general random-access curve: seek replays must
/// create a fresh invocation and reconstruct the timeline from frame zero.
/// </summary>
internal sealed class FmSourcePitchCurve : PitchCurve
{
    private long _frame = -1;
    private double _previous = 1;
    private double _current = 1;
    private bool _hasRate;
    private bool _hasVaried;

    // A direct Sample source can transfer its integrated playback position
    // in O(1) only while every authored pitch step has the same rate.
    // Otherwise an onset-offset change requires historical replay.
    internal bool HasConstantRateHistory => _hasRate && !_hasVaried;
    internal double LastRate => _current;
    internal long LastFrame => _frame;

    /// <summary>Attach the worker-created pitch curve to a Sample state
    /// whose full accumulated position was seeded at the PCM boundary.
    /// The next SetFrame must be precisely nextFrame; no old frames are
    /// evaluated, replayed or retained from the outgoing invocation.</summary>
    internal void SeedBeforeFrame(long nextFrame, double previousRate,
        bool hasVariedRateHistory = false)
    {
        if (nextFrame < 0 || !(previousRate > 0.0)
            || !double.IsFinite(previousRate))
            throw new ArgumentOutOfRangeException(nameof(nextFrame));
        _frame = nextFrame - 1;
        _previous = _current = previousRate;
        _hasRate = true;
        // Preserve the source's historical variability even though its
        // exact integrated position has been moved into the new clock.
        // Later phase edits must not mistake a variable-rate past for a
        // single constant segment merely because this curve was seeded.
        _hasVaried = hasVariedRateHistory;
    }

    public void SetFrame(long frame, double multiplier)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        if (!(multiplier > 0) || !double.IsFinite(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        if (frame == _frame)
        {
            if (_hasRate && multiplier != _current)
                _hasVaried = true;
            _current = multiplier;
            _hasRate = true;
            return;
        }
        if (frame != _frame + 1)
            throw new InvalidOperationException(
                "A Synth source pitch curve must advance one frame at a time.");
        if (_hasRate && multiplier != _current)
            _hasVaried = true;
        _previous = _current;
        _current = multiplier;
        _frame = frame;
        _hasRate = true;
    }

    public override double GetMultiplier(long frameOffset)
    {
        if (frameOffset == _frame)
            return _current;
        if (frameOffset == _frame - 1)
            return _previous;
        throw new InvalidOperationException(
            "Source pitch history must be replayed in order after a seek.");
    }
}
