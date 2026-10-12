using System;
using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

/// <summary>
/// Programmable tone selection from a finite, declared list of sources.
/// The persisted expression returns a zero-based specification index, or -1
/// for silence. Explicit specifications preserve all references for import,
/// tombstones and deterministic snapshot binding.
/// </summary>
public sealed class ScriptInstrumentDefinition : InstrumentDefinitionBase
{
	public ScriptInstrumentDefinition(ObjectId id, string name) : base(id, name) { }

    /// <summary>Restricted C# expression of incoming pitchMultiplier.
    /// Returns InstrumentNnaAction.Inherit or Cut/Continue/Off/Fade;
    /// compiled at source preparation, never in the PCM callback.</summary>
    public string NnaExpression { get; set; } = "InstrumentNnaAction.Inherit";

    /// <summary>Positive duration used if the scripted NNA returns Fade.</summary>
    public TimeSpan NnaFadeDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    public override InstrumentNnaSetting ResolveNna(
        double pitchMultiplier, ToneSpecification selectedTone,
        Func<double, InstrumentNnaAction>? scriptedEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(selectedTone);
        InstrumentNnaAction action = scriptedEvaluator?.Invoke(pitchMultiplier)
            ?? InstrumentNnaAction.Inherit;
        // An inherited script result delegates to the selected tone;
        // that tone may itself inherit the terminal source's NNA.
        InstrumentNnaSetting resolved = action == InstrumentNnaAction.Inherit
            ? selectedTone.Nna
            : new InstrumentNnaSetting(action,
                action == InstrumentNnaAction.Fade ? NnaFadeDuration : null);
        resolved.Validate();
        return resolved;
    }

	/// <summary>Restricted C# integer expression of pitchMultiplier (C4=1).</summary>
	public string Source { get; set; } = "-1";

	/// <summary>
	/// Restricted scalar expression of pitchMultiplier, multiplied by the
	/// chosen tone's stored pitch ratio on each invocation.
	/// </summary>
	public string PitchExpression { get; set; } = "1.0";

	/// <summary>
	/// Source, pitch ratio, and per-target optional Envelope overrides.
	/// Selector results are indices into this list; no dynamic ID creation.
	/// </summary>
	public List<ToneSpecification> ToneSpecifications { get; } = [];
}
