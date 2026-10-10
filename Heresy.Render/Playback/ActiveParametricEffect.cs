using System;
using Heresy.Core.Sequencing;
using Heresy.Render.Timing;
using Heresy.Render.Clock;

namespace Heresy.Render.Playback;

/// <summary>
/// One independently composited native wall-time effect. Its scalar expression
/// is compiled from the frozen song before rendering. Evaluate at absolute
/// frame coordinates; expire at a deterministic exclusive frame boundary.
/// </summary>
internal sealed class ActiveParametricEffect
{
    private readonly TrackerTickClock _clock;
    private readonly int _sampleRate;
    public ParametricEffectParameter Parameter { get; }
    public long StartFrame { get; }
    public long EndFrame { get; }
    public FunctionalRowPlaybackOperator Operator { get; }

    public ActiveParametricEffect(
        StartParametricEffectCommand command,
        long startFrame,
        int sampleRate,
        TrackerTickClock clock)
    {
        if (command.CompiledEvaluator is null)
            throw new InvalidOperationException(
                "Parametric expression must be compiled before rendering.");
        if (command.Duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(command));
        if (!Enum.IsDefined(command.Parameter))
            throw new ArgumentOutOfRangeException(nameof(command));
        Parameter = command.Parameter;
        StartFrame = startFrame;
        _sampleRate = sampleRate;
        _clock = clock;
        EndFrame = checked(startFrame + FrameTime.Ceiling(command.Duration, sampleRate));
        PlaybackParameter coordinate = command.Parameter switch
        {
            ParametricEffectParameter.GlobalVolume => PlaybackParameter.GlobalVolume,
            ParametricEffectParameter.OverallChannelVolume => PlaybackParameter.OverallVolume,
            ParametricEffectParameter.SpatialX => PlaybackParameter.SpatialX,
            _ => throw new NotSupportedException(),
        };
        Operator = new FunctionalRowPlaybackOperator(
            coordinate, command.CompiledEvaluator, command.CommitOnExpire);
    }

    /// <summary>True after the operator's final endpoint is evaluated.</summary>
    public bool Advance(long frame, PlaybackOperatorCollection collection,
        out PlaybackParameterDeltas committed)
    {
        long at = Math.Min(Math.Max(StartFrame, frame), EndFrame);
        double seconds = (at - StartFrame) / (double)_sampleRate;
        double ticks = _clock.GetElapsedTicks(StartFrame, at);
        if (frame >= EndFrame)
        {
            committed = collection.Expire(Operator, seconds, ticks);
            return true;
        }
        Operator.Update(seconds, ticks);
        committed = new PlaybackParameterDeltas();
        return false;
    }
}
