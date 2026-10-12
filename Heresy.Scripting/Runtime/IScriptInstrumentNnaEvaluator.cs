using Heresy.Core.Instruments;

namespace Heresy.Scripting.Runtime;

/// <summary>Stateless, prepared script result evaluated from the same
/// positive finite incoming pitch as the scripted tone selector.
/// Returned enum is validated by the Instrument NNA contract.</summary>
public interface IScriptInstrumentNnaEvaluator
{
    InstrumentNnaAction SelectNna(double pitchMultiplier);
}
