using System;

namespace Heresy.Render.FmSynthesis;

/// <summary>One 100 ms smoothstep clock for compatible live Synth
/// migrations, interpreted in each parameter's natural domain.</summary>
internal static class FmLiveTransitionClock
{
    internal const int DurationMilliseconds = 100;

    internal static int Frames(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return Math.Max(1, (int)Math.Min(int.MaxValue,
            (long)sampleRate * DurationMilliseconds / 1000));
    }

    internal static double WeightFrames(long start, long frame, int duration)
    {
        double t = Math.Clamp((double)(frame - start) / duration, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    internal static double Weight(long start, long frame, int sampleRate)
        => WeightFrames(start, frame, Frames(sampleRate));

    internal static double Frequency(double startHz, double endHz, double weight)
    {
        if (weight <= 0.0) return startHz;
        if (weight >= 1.0) return endHz;
        if (!(startHz > 0.0) || !(endHz > 0.0))
            return startHz;
        return Math.Exp2(Math.Log2(startHz)
            + (Math.Log2(endHz) - Math.Log2(startHz)) * weight);
    }
}
