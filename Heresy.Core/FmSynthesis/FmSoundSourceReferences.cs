using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.Core.FmSynthesis;

/// <summary>
/// Structural preflight for an ISound-fed Synth graph. Objects are visited
/// across both nested Synth and Instrument edges so an otherwise acyclic
/// numeric graph cannot recursively instantiate itself through ObjectIds.
/// This works on the frozen song or on a candidate graph BEFORE committing
/// an authoring mutation. Unassigned/deleted IDs are inert references, not
/// substitutes for live objects.
/// </summary>
public static class FmSoundSourceReferences
{
    public static void Validate(
        SongDocument document,
        ObjectId ownerId,
        FmSynthGraph candidate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidate);
        if (ownerId.IsNone)
            throw new ArgumentException("An owning Synth ID is required.", nameof(ownerId));

        HashSet<ObjectId> visiting = [];
        HashSet<ObjectId> visited = [];
        Visit(ownerId);

        void Visit(ObjectId id)
        {
            if (id.IsNone || visited.Contains(id))
                return;
            if (!visiting.Add(id))
                throw new InvalidOperationException(
                    $"Synth source reference cycle includes object #{id.Value}.");

            SongObject? obj;
            if (id == ownerId)
            {
                // The supplied candidate must win over any old persisted graph.
                foreach (FmSoundSourceNode source in candidate.Nodes
                    .OfType<FmSoundSourceNode>())
                    Visit(source.SourceId);
            }
            else if (document.TryGet(id, out obj) && obj is not null)
            {
                switch (obj)
                {
                    case FmSynthDefinition synth:
                        foreach (FmSoundSourceNode source in synth.Graph.Nodes
                            .OfType<FmSoundSourceNode>())
                            Visit(source.SourceId);
                        break;
                    case InstrumentDefinition instrument:
                        foreach (ToneSpecification tone in instrument.ToneSpecifications)
                            Visit(tone.SourceId);
                        break;
                    case ScriptInstrumentDefinition scriptInstrument:
                        foreach (ToneSpecification tone in scriptInstrument.ToneSpecifications)
                            Visit(tone.SourceId);
                        break;
                    case SampleDefinition:
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Object #{id.Value} is not a directly invocable ISound source.");
                }
            }
            // Missing references stay representable, and will be silent.
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
