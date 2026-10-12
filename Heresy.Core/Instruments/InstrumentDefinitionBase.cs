using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

/// <summary>Common Instrument note-start NNA resolution contract. The
/// immutable incoming pitch is the exact input used for tone selection;
/// a scripted implementation receives its precompiled pure evaluator.</summary>
public abstract class InstrumentDefinitionBase(ObjectId id, string name)
    : SongObject(id, name)
{
    public sealed override SongObjectKind Kind => SongObjectKind.Instrument;

    public abstract InstrumentNnaSetting ResolveNna(
        double pitchMultiplier, ToneSpecification selectedTone,
        Func<double, InstrumentNnaAction>? scriptedEvaluator = null);
}
