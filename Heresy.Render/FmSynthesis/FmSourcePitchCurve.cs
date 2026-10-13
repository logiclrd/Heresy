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
