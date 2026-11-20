using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Fully detached graph-aware Duplicate (deep) proposal. Each original has one
/// mapped identity; Candidates are in contiguous, newly allocated ID order,
/// beginning with the selected root. No song ID or tree node is reserved
/// before a successful whole-batch Commit.
/// </summary>
public sealed class SongObjectDeepDuplicateDraft
{
    internal SongObjectDeepDuplicateDraft(
        SongDocument document, ObjectId sourceId, ObjectId expectedNextId,
        string authoringSnapshot, Dictionary<ObjectId, ObjectId> ids,
        IReadOnlyList<SongObject> candidates)
    {
        Document = document;
        SourceId = sourceId;
        ExpectedNextId = expectedNextId;
        AuthoringSnapshot = authoringSnapshot;
        IdMap = new ReadOnlyDictionary<ObjectId, ObjectId>(
            new Dictionary<ObjectId, ObjectId>(ids));
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    internal SongDocument Document { get; }
    internal string AuthoringSnapshot { get; }

    public ObjectId SourceId { get; }
    public ObjectId ExpectedNextId { get; }
    public IReadOnlyDictionary<ObjectId, ObjectId> IdMap { get; }
    public IReadOnlyList<SongObject> Candidates { get; }
    public SongObject RootCandidate => Candidates[0];
}

/// <summary>
/// D3/D4: cycle-safe deep duplication of typed and semantically recognized
/// scripted dependencies. The complete closure is discovered before new IDs
/// are assigned; all references use the finalized shared ID mapping. Unreliable
/// scripted reference analysis always refuses before publication.
/// </summary>
public static class SongObjectDeepDuplication
{
    public static SongObjectDeepDuplicateDraft Prepare(
        SongDocument document, ObjectId sourceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.TryGet(sourceId, out SongObject? root) || root is null)
            throw new InvalidOperationException(
                $"Cannot duplicate missing song object #{sourceId.Value}.");
        if (root is not (InstrumentDefinition or ScriptInstrumentDefinition
            or FmSynthDefinition or ScriptSequenceDefinition
            or ScriptPatternDefinition or ScriptEnvelopeDefinition))
            throw new NotSupportedException(
                $"Duplicate (deep) is not supported for {root.GetType().Name}.");

        ObjectId expectedNextId = document.NextUnallocatedObjectId;
        if (expectedNextId.IsNone)
            throw new InvalidOperationException("Song object IDs are exhausted.");
        string snapshot = SongDocumentJson.SerializeAuthoringSnapshot(document);

        // Iterative, not recursive: a graph may contain repeated targets,
        // diamonds or self-links, even when editor-created cycles are banned.
        HashSet<ObjectId> discovered = [sourceId];
        Queue<ObjectId> remaining = new();
        remaining.Enqueue(sourceId);
        while (remaining.TryDequeue(out ObjectId current))
        {
            SongObject node = document.Objects[current];
            switch (node)
            {
                case ScriptInstrumentDefinition instrument:
                    // Scripted Instruments traverse ALL authored typed references,
                    // not only sound sources, as well as all three script fields.
                    foreach (ToneSpecification tone in instrument.ToneSpecifications)
                    {
                        Visit(tone.SourceId, "Script Instrument tone source",
                            IsSoundSource);
                        VisitOptional(tone.VolumeEnvelopeId, "Script Instrument volume Envelope",
                            item => item is EnvelopeDefinition);
                        VisitOptional(tone.PitchEnvelopeId, "Script Instrument pitch Envelope",
                            item => item is EnvelopeDefinition);
                        VisitOptional(tone.PanningEnvelopeId, "Script Instrument panning Envelope",
                            item => item is EnvelopeDefinition);
                        VisitOptional(tone.FilterEnvelopeId, "Script Instrument filter Envelope",
                            item => item is EnvelopeDefinition);
                        if (tone.FilterConfiguration is FilterInstance configuration)
                            Visit(configuration.DefinitionId, "Script Instrument Filter",
                                item => item is FilterDefinition);
                    }
                    break;
                case InstrumentDefinition instrument:
                    foreach (ToneSpecification tone in instrument.ToneSpecifications)
                        Visit(tone.SourceId, "Instrument tone source",
                            IsSoundSource);
                    break;
                case FmSynthDefinition synth:
                    foreach (FmSynthNode graphNode in synth.Graph.Nodes)
                    {
                        switch (graphNode)
                        {
                            case FmSoundSourceNode sound:
                                Visit(sound.SourceId, "Synth sound source",
                                    IsSoundSource);
                                break;
                            case FmEnvelopeNode envelope:
                                Visit(envelope.EnvelopeId, "Synth envelope",
                                    item => item is EnvelopeDefinition);
                                break;
                            case FmFilterNode filter
                                when filter.Configuration is FilterInstance configuration:
                                Visit(configuration.DefinitionId, "Synth Filter",
                                    item => item is FilterDefinition);
                                break;
                        }
                    }
                    break;
                case ScriptEnvelopeDefinition:
                case ScriptPatternDefinition:
                case ScriptSequenceDefinition:
                    // These definitions have script edges only; their data
                    // equivalents remain terminal even in a deep operation.
                    break;
                case SampleDefinition:
                case FilterDefinition:
                case NodeEnvelopeDefinition:
                case DataSequenceDefinition:
                case DataPatternDefinition:
                    // Terminal by specification, even when a Data Pattern or
                    // Sequence owns source/pattern references.
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported deep dependency type {node.GetType().Name}.");
            }

            // The semantic parser excludes comments, strings and shadowed
            // functions. Every recognized _O target is a graph edge, even a
            // back-edge or self-reference. Malformed/nonliteral references
            // cause an atomic refusal rather than a partial dependency set.
            foreach (ObjectId target in SongObjectScriptReferences.Discover(node))
                Visit(target, "Script _O reference", _ => true);
        }

        void VisitOptional(
            ObjectId? targetId, string slot, Func<SongObject, bool> valid)
        {
            if (targetId.HasValue)
                Visit(targetId.Value, slot, valid);
        }

        void Visit(ObjectId targetId, string slot, Func<SongObject, bool> valid)
        {
            if (targetId.IsNone)
                return; // An intentionally unassigned input is not an object.
            if (!document.TryGet(targetId, out SongObject? target)
                || target is null)
                throw new InvalidOperationException(
                    $"{slot} refers to missing object #{targetId.Value}.");
            if (!valid(target))
                throw new InvalidOperationException(
                    $"{slot} references incompatible object #{targetId.Value}"
                    + $" ({target.GetType().Name}).");
            if (discovered.Add(targetId))
                remaining.Enqueue(targetId);
        }

        // Always assign the root first, then dependencies by original ID,
        // regardless of graph traversal order. SongDocument's atomic batch
        // requires candidate IDs to be contiguous in published array order.
        ObjectId[] originals = [sourceId, .. discovered.Where(id => id != sourceId)
            .OrderBy(id => id.Value)];
        ulong lastId = (ulong)expectedNextId.Value
            + (ulong)originals.Length - 1UL;
        if (lastId > uint.MaxValue)
            throw new InvalidOperationException(
                "Deep duplication exceeds the song's available object ID range.");

        Dictionary<ObjectId, ObjectId> ids = new();
        for (int i = 0; i < originals.Length; i++)
        {
            ObjectId candidateId = new(
                (uint)((ulong)expectedNextId.Value + (ulong)i));
            if (document.Objects.ContainsKey(candidateId)
                || document.Tombstones.ContainsKey(candidateId))
                throw new InvalidOperationException(
                    $"Proposed object ID #{candidateId.Value} is already occupied.");
            ids.Add(originals[i], candidateId);
        }

        // Batch names are not merely individually unique against the source
        // song: earlier proposed dependency names also occupy the namespace.
        Dictionary<SongObjectKind, HashSet<string>> namesByKind = [];
        Dictionary<ObjectId, string> proposedNames = [];
        foreach (ObjectId originalId in originals)
        {
            SongObject source = document.Objects[originalId];
            if (!namesByKind.TryGetValue(source.Kind,
                out HashSet<string>? names))
            {
                names = document.Objects.Values
                    .Where(item => item.Kind == source.Kind)
                    .Select(item => item.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                namesByKind.Add(source.Kind, names);
            }
            string name = SongObjectDuplication.SuggestName(source.Name, names);
            if (!names.Add(name))
                throw new InvalidOperationException(
                    "Duplicate name planner generated a collision.");
            proposedNames.Add(originalId, name);
        }

        // Filter uses refer to FilterDefinition *objects*, not just IDs.
        // Materialize every cloned Filter before consumers so a copied tone
        // or graph node can bind to the new Filter without mutating either.
        Dictionary<ObjectId, FilterDefinition> clonedFilters = [];
        foreach (ObjectId originalId in originals)
        {
            if (document.Objects[originalId] is FilterDefinition filter)
            {
                FilterDefinition copy = (FilterDefinition)
                    SongObjectDuplication.CloneWithReferences(
                        filter, ids[originalId], proposedNames[originalId], ids);
                clonedFilters.Add(originalId, copy);
            }
        }

        SongObject[] candidates = originals.Select(id =>
            document.Objects[id] is FilterDefinition
                ? clonedFilters[id]
                : SongObjectDuplication.CloneWithReferences(document.Objects[id],
                    ids[id], proposedNames[id], ids, clonedFilters)).ToArray();

        // After all candidate identities and typed references exist, rewrite
        // every script field by Roslyn's semantic reference spans. Scripts
        // are never changed on the original objects, and repeated targets
        // always reuse the same single new ObjectId.
        for (int index = 0; index < originals.Length; index++)
            SongObjectScriptReferences.Rewrite(
                document.Objects[originals[index]], candidates[index], ids);

        // A Sample asset may have changed during a slow file read. Check that
        // the source song's authoring state stayed consistent during Prepare.
        if (document.NextUnallocatedObjectId != expectedNextId
            || !string.Equals(snapshot,
                SongDocumentJson.SerializeAuthoringSnapshot(document),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The song changed while the deep duplicate was being prepared.");

        return new SongObjectDeepDuplicateDraft(document, sourceId,
            expectedNextId, snapshot, ids, candidates);
    }

    public static SongImportBatchEdit Commit(
        SongObjectDeepDuplicateDraft draft, SongTreeObject? selectedOccurrence = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        // Every detached Sample is revalidated before atomic publication,
        // including a source that changed after an earlier prepared Sample.
        foreach ((ObjectId original, ObjectId copied) in draft.IdMap)
        {
            SongObject copy = draft.Candidates.Single(item => item.Id == copied);
            if (copy is SampleDefinition sample)
                SongObjectDuplication.VerifySampleCandidate(
                    draft.Document, original, sample);
        }

        IReadOnlyList<SongImportTreePlacement?>? placements =
            selectedOccurrence is null
                ? null
                : SongObjectDuplicateTreePlacement.Plan(
                    draft.Document, selectedOccurrence, draft.SourceId,
                    draft.Candidates, draft.IdMap);
        return draft.Document.CommitImportedObjects(
            draft.Candidates, draft.ExpectedNextId,
            doc => doc.TryGet(draft.SourceId, out SongObject? root)
                && root is not null
                && string.Equals(
                    SongDocumentJson.SerializeAuthoringSnapshot(doc),
                    draft.AuthoringSnapshot, StringComparison.Ordinal),
            placements);
    }

    private static bool IsSoundSource(SongObject obj)
        => obj is SampleDefinition or FmSynthDefinition
            or InstrumentDefinition or ScriptInstrumentDefinition;
}
