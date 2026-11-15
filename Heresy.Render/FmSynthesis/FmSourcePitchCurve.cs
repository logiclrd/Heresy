using System;

using Heresy.Render.Sounds;

namespace Heresy.Render.FmSynthesis;

/// <summary>
/// Allocation-free chronological Source pitch curve. SampleSound integrates
/// the previous frame's multiplier before reading the current one, so two
/// adjacent rates are enough to keep the current playhead advancing.
/// Live edits preserve the accumulated position, not a historical rate log.
/// </summary>
internal sealed class FmSourcePitchCurve : PitchCurve
{
    private long _frame = -1;
    private double _previous = 1;
    private double _current = 1;

    internal double LastRate => _current;
    internal long LastFrame => _frame;

    /// <summary>Attach a detached, prepared Source curve to a Sample whose
    /// actual fractional position was seeded at its revised local frame.
    /// The next SetFrame advances from there without replaying history.</summary>
    internal void SeedBeforeFrame(long nextFrame, double previousRate)
    {
        if (nextFrame < 0 || !(previousRate > 0.0)
            || !double.IsFinite(previousRate))
            throw new ArgumentOutOfRangeException(nameof(nextFrame));
        _frame = nextFrame - 1;
        _previous = _current = previousRate;
    }

    public void SetFrame(long frame, double multiplier)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        if (!(multiplier > 0) || !double.IsFinite(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        if (frame == _frame)
        {
            _current = multiplier;
            return;
        }
        if (frame != _frame + 1)
            throw new InvalidOperationException(
                "A Synth source pitch curve must advance one frame at a time.");
        _previous = _current;
        _current = multiplier;
        _frame = frame;
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
