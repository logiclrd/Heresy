using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Envelopes;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// UI policy and tree placement for shallow and deep duplication. The root
/// follows the exact clicked occurrence, not an arbitrary other reference to
/// the same object. Dependencies follow the first existing occurrence in
/// deterministic depth-first tree order, or the section root if unplaced.
/// This only plans positions; SongDocument commits them atomically.
/// </summary>
public static class SongObjectDuplicateTreePlacement
{
    public static bool HasDeepOption(SongObject source)
        => source is InstrumentDefinition or ScriptInstrumentDefinition
            or FmSynthDefinition or ScriptPatternDefinition
            or ScriptSequenceDefinition or ScriptEnvelopeDefinition;

    public static IReadOnlyList<SongImportTreePlacement?> Plan(
        SongDocument document, SongTreeObject selectedOccurrence,
        ObjectId originalRoot,
        IReadOnlyList<SongObject> candidates,
        IReadOnlyDictionary<ObjectId, ObjectId> idMap)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedOccurrence);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(idMap);
        if (selectedOccurrence.ObjectId != originalRoot)
            throw new InvalidOperationException(
                "The selected tree occurrence does not identify the duplicate root.");
        SongTreeFolder selectedParent = FindParent(document.Root, selectedOccurrence)
            ?? throw new InvalidOperationException(
                "The selected tree occurrence is no longer attached.");
        if (candidates.Count != idMap.Count
            || candidates.Count == 0
            || candidates[0].Id != idMap[originalRoot])
            throw new InvalidOperationException(
                "The duplicated candidates are not aligned with their source ID map.");

        Dictionary<ObjectId, ObjectId> reversed = idMap
            .ToDictionary(pair => pair.Value, pair => pair.Key);
        SongImportTreePlacement?[] targets =
            new SongImportTreePlacement?[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            SongObject copy = candidates[index];
            if (!reversed.TryGetValue(copy.Id, out ObjectId originalId)
                || !document.TryGet(originalId, out SongObject? source)
                || source is null
                || source.Kind != copy.Kind)
                throw new InvalidOperationException(
                    "A duplicate candidate no longer has a corresponding source.");
            if (index == 0)
            {
                targets[index] = new SongImportTreePlacement(
                    selectedParent, selectedOccurrence);
            }
            else
            {
                SongTreeFolder sectionRoot = document.GetSectionRoot(
                    SongTreeSections.ForKind(copy.Kind));
                (SongTreeFolder Parent, SongTreeObject Node)? existing =
                    FindFirstOccurrence(sectionRoot, originalId);
                targets[index] = existing is { } found
                    ? new SongImportTreePlacement(found.Parent, found.Node)
                    : new SongImportTreePlacement(sectionRoot);
            }
        }
        return Array.AsReadOnly(targets);
    }

    public static SongTreeObject FindCommittedRoot(
        SongDocument document, ObjectId duplicateId,
        SongTreeObject sourceOccurrence)
    {
        SongTreeFolder parent = FindParent(document.Root, sourceOccurrence)
            ?? throw new InvalidOperationException(
                "The duplicate source occurrence is no longer attached.");
        int originalIndex = parent.Children.IndexOf(sourceOccurrence);
        if (originalIndex < 0 || originalIndex + 1 >= parent.Children.Count
            || parent.Children[originalIndex + 1] is not SongTreeObject result
            || result.ObjectId != duplicateId)
            throw new InvalidOperationException(
                "The committed duplicate is not adjacent to its selected source.");
        return result;
    }

    private static (SongTreeFolder Parent, SongTreeObject Node)?
        FindFirstOccurrence(SongTreeFolder parent, ObjectId id)
    {
        foreach (SongTreeNode child in parent.Children)
        {
            if (child is SongTreeObject node && node.ObjectId == id)
                return (parent, node);
            if (child is SongTreeFolder folder
                && FindFirstOccurrence(folder, id) is { } found)
                return found;
        }
        return null;
    }

    private static SongTreeFolder? FindParent(
        SongTreeFolder parent, SongTreeNode target)
    {
        foreach (SongTreeNode child in parent.Children)
        {
            if (ReferenceEquals(child, target))
                return parent;
            if (child is SongTreeFolder folder
                && FindParent(folder, target) is SongTreeFolder result)
                return result;
        }
        return null;
    }
}
