using System;
using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

public sealed class InstrumentDefinition : InstrumentDefinitionBase
{
	public InstrumentDefinition(ObjectId id, string name) : base(id, name) { }

    /// <summary>Blanket override; UseToneNna preserves each selected tone's
    /// explicit policy, or its child's inherited policy.</summary>
    public InstrumentNnaOverride NnaOverride { get; set; } =
        InstrumentNnaOverride.UseToneNna;

    /// <summary>Duration used only if the blanket NNA is Fade.</summary>
    public TimeSpan NnaFadeDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    public override InstrumentNnaSetting ResolveNna(
        double pitchMultiplier, ToneSpecification selectedTone,
        Func<double, InstrumentNnaAction>? scriptedEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(selectedTone);
        if (!Enum.IsDefined(NnaOverride))
            throw new ArgumentOutOfRangeException(nameof(NnaOverride));
        InstrumentNnaSetting resolved = NnaOverride switch
        {
            InstrumentNnaOverride.UseToneNna => selectedTone.Nna,
            InstrumentNnaOverride.Cut => new(InstrumentNnaAction.Cut),
            InstrumentNnaOverride.Continue => new(InstrumentNnaAction.Continue),
            InstrumentNnaOverride.Off => new(InstrumentNnaAction.Off),
            InstrumentNnaOverride.Fade => new(InstrumentNnaAction.Fade, NnaFadeDuration),
            _ => throw new InvalidOperationException(),
        };
        resolved.Validate();
        return resolved;
    }

	/// <summary>Divisions applied to log2(pitch relative to middle C).</summary>
	public double Divisions { get; set; } = 12.0;

	/// <summary>Offset added after pitch quantization to obtain a tone-table index.</summary>
	public int Offset { get; set; }

	public List<ToneSpecification> ToneSpecifications { get; } = [];

	/// <summary>
	/// Maps tone indices to ToneSpecifications. A value of -1 means silent.
	/// Several entries may point to the same specification.
	/// </summary>
	public List<int> ToneTable { get; } = [];
}
