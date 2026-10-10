namespace Heresy.Scripting.Runtime;

/// <summary>Pure, compiled per-invocation pitch multiplier expression.</summary>
public interface IScriptInstrumentPitchEvaluator
{
    double GetPitchScale(double pitchMultiplier);
}
