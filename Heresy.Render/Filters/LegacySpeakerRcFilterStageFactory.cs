using System;
using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>Exact compatibility processing for old output-speaker RC choices.
/// Unlike FilterKind.OnePole's bilinear/TPT transfer function, the legacy
/// speaker recurrence has alpha=1-exp(-2*pi*cutoff/sampleRate) and LP state
/// s'=s+alpha*(input-s); HP=input-s'. This factory is selected only for
/// migrated output-speaker preferences, never the general One-Pole Filter.</summary>
public sealed class LegacySpeakerRcFilterStageFactory : IStreamingFilterStageFactory
{
    public static LegacySpeakerRcFilterStageFactory Instance { get; } = new();
    private LegacySpeakerRcFilterStageFactory() { }

    public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Kind != FilterKind.OnePole || sampleRate <= 0)
            throw new ArgumentException("Legacy speaker RC requires OnePole and valid sample rate.");
        double cutoff = stage.Parameters["CutoffHz"].NumericValue;
        if (!double.IsFinite(cutoff) || cutoff <= 0 || cutoff >= sampleRate * 0.5)
            throw new ArgumentOutOfRangeException(nameof(stage), "Speaker cutoff must be below Nyquist.");
        string mode = stage.Parameters["Mode"].ChoiceValue
            ?? throw new ArgumentException("Speaker RC requires a Mode.");
        if (mode is not ("LowPass" or "HighPass"))
            throw new ArgumentException("Unknown speaker RC mode.");
        double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sampleRate);
        double decay = 1.0 - alpha;
        double frames = decay <= 0 ? 2
            : Math.Ceiling(Math.Log(1e-7) / Math.Log(decay)) + 2;
        int tail = !double.IsFinite(frames) || frames >= int.MaxValue
            ? int.MaxValue : Math.Max(2, (int)frames);
        return new Plan(alpha, mode == "HighPass", tail);
    }

    private sealed class Plan(double alpha, bool highPass, int tail)
        : IStreamingFilterStagePlan
    {
        public double Alpha => alpha;
        public bool HighPass => highPass;
        public int TailFrames => tail;
        public IStreamingFilterStageState CreateState() => new State(this, 0);
    }

    private sealed class State(Plan plan, double low) : IStreamingFilterStageState
    {
        private Plan _plan = plan;
        private double _low = low;

        public float Process(float sample)
        {
            double input = sample;
            double low = _low + _plan.Alpha * (input - _low);
            if (input == 0 && Math.Abs(low) <= 1e-7)
                low = 0;
            _low = low;
            return (float)(_plan.HighPass ? input - low : low);
        }

        public IStreamingFilterStageState Clone() => new State(_plan, _low);
        public void Reconfigure(IStreamingFilterStagePlan plan)
        {
            _plan = plan as Plan ??
                throw new ArgumentException("Incompatible legacy RC Filter plan.");
        }
    }
}
