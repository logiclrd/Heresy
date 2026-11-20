using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A detached, non-mutating proposal. The candidate owns a new object identity
/// and independent authoring collections, but deliberately preserves same-song
/// references to existing objects. It is not yet a tree-command/UI operation.
/// </summary>
public sealed class SongObjectDuplicateDraft
{
    internal SongObjectDuplicateDraft(
        SongDocument document, ObjectId sourceId, ObjectId expectedId,
        SongObject candidate, string authoringSnapshot)
    {
        Document = document;
        SourceId = sourceId;
        ExpectedId = expectedId;
        Candidate = candidate;
        AuthoringSnapshot = authoringSnapshot;
    }

    internal SongDocument Document { get; }
    internal string AuthoringSnapshot { get; }
    public ObjectId SourceId { get; }
    public ObjectId ExpectedId { get; }
    public SongObject Candidate { get; }
}

/// <summary>
/// First slice of same-song Duplicate Object: identity, naming, detached
/// authoring-copy semantics and existing atomic commit/undo. Supports the
/// Pattern, Sequence, Envelope and Filter definition families. Other families
/// deliberately fail before ID reservation or publication until their
/// Sample asset and Synth/Instrument reference semantics are implemented.
/// </summary>
public static class SongObjectDuplication
{
    public static SongObjectDuplicateDraft Prepare(
        SongDocument document, ObjectId sourceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.TryGet(sourceId, out SongObject? source)
            || source is null)
            throw new InvalidOperationException(
                $"Cannot duplicate missing song object #{sourceId.Value}.");

        ObjectId next = document.NextUnallocatedObjectId;
        if (next.IsNone)
            throw new InvalidOperationException("Object IDs are exhausted.");
        string expectedSnapshot =
            SongDocumentJson.SerializeAuthoringSnapshot(document);
        string name = SuggestName(document, source);
        SongObject clone = Clone(source, next, name);
        return new(document, sourceId, next, clone, expectedSnapshot);
    }

    /// <summary>
    /// An accepted draft publishes as one atomic object edit, with a guarded
    /// Undo/Redo receipt. The tree placement currently defaults to the
    /// appropriate section root; sibling-folder placement and UI history
    /// wiring are separate follow-up slices.
    /// </summary>
    public static SongImportBatchEdit Commit(SongObjectDuplicateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Document.CommitImportedObjects(
            [draft.Candidate], draft.ExpectedId,
            document =>
                document.TryGet(draft.SourceId, out SongObject? source)
                && source is not null
                && string.Equals(
                    SongDocumentJson.SerializeAuthoringSnapshot(document),
                    draft.AuthoringSnapshot, StringComparison.Ordinal));
    }

    /// <summary>Use the next free terminal numeric suffix rather than
    /// appending " 2" to a name which already ends in " 3". Names are
    /// case-insensitive for suggestions, but never a uniqueness invariant.</summary>
    public static string SuggestName(SongDocument document, SongObject source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        HashSet<string> occupied = document.Objects.Values
            .Where(item => item.Kind == source.Kind)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string original = source.Name.Trim();
        if (original.Length == 0)
            throw new InvalidOperationException(
                "An unnamed song object cannot be duplicated.");

        string stem = original;
        BigInteger suffix = 2;
        int lastSpace = original.LastIndexOf(' ');
        if (lastSpace > 0 && lastSpace < original.Length - 1
            && BigInteger.TryParse(original.AsSpan(lastSpace + 1),
                NumberStyles.None, CultureInfo.InvariantCulture,
                out BigInteger parsed))
        {
            stem = original[..lastSpace];
            suffix = parsed + 1;
        }
        string candidate;
        do
        {
            candidate = $"{stem} {suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        } while (occupied.Contains(candidate));
        return candidate;
    }

    private static SongObject Clone(SongObject source, ObjectId id, string name)
        => source switch
        {
            DataSequenceDefinition sequence => CloneSequence(sequence, id, name),
            ScriptSequenceDefinition script => new ScriptSequenceDefinition(id, name)
            {
                Source = script.Source,
            },
            DataPatternDefinition pattern => ClonePattern(pattern, id, name),
            ScriptPatternDefinition script => CopyPatternProperties(
                script, new ScriptPatternDefinition(id, name)
                {
                    Source = script.Source,
                }),
            NodeEnvelopeDefinition or ScriptEnvelopeDefinition
                => CloneEnvelope((EnvelopeDefinition)source, id, name),
            FilterDefinition filter => CloneFilter(filter, id, name),
            _ => throw new NotSupportedException(
                $"Duplicate Object does not yet support {source.GetType().Name}."),
        };

    private static DataSequenceDefinition CloneSequence(
        DataSequenceDefinition source, ObjectId id, string name)
    {
        DataSequenceDefinition copy = new(id, name);
        copy.Entries.AddRange(source.Entries.Select(
            item => new SequenceEntry(item.PatternId, item.StartRow)));
        return copy;
    }

    private static T CopyPatternProperties<T>(PatternDefinition source, T copy)
        where T : PatternDefinition
    {
        copy.RowCount = source.RowCount;
        copy.ChannelCount = source.ChannelCount;
        copy.MinorHighlightRows = source.MinorHighlightRows;
        copy.MajorHighlightRows = source.MajorHighlightRows;
        copy.CopySourceColourFrom(source);
        return copy;
    }

    private static DataPatternDefinition ClonePattern(
        DataPatternDefinition source, ObjectId id, string name)
    {
        DataPatternDefinition copy = CopyPatternProperties(
            source, new DataPatternDefinition(id, name));
        foreach ((int row, int column, PatternCell cell)
            in source.Grid.EnumerateNonEmptyCells())
        {
            PatternCell copied = copy.Grid.GetOrCreateCell(row, column);
            copied.Note = cell.Note is null ? null : cell.Note with { };
            copied.SourceId = cell.SourceId;
            copied.Volume = cell.Volume;
            copied.Effects.AddRange(cell.Effects.Select(effect => effect with { }));
        }
        return copy;
    }

    private static EnvelopeDefinition CloneEnvelope(
        EnvelopeDefinition source, ObjectId id, string name)
    {
        EnvelopeDefinition cloned = FmSynthDocumentEditor.CloneEnvelope(source, id);
        cloned.Name = name;
        return cloned;
    }

    private static FilterDefinition CloneFilter(
        FilterDefinition source, ObjectId id, string name)
    {
        FilterDefinition cloned = new(id, name,
            source.Stages.Select(stage => new FilterStageDefinition(
                stage.StageId, stage.Name, stage.Kind, stage.DefaultParameters)));
        cloned.RestoreStageIdentity(
            source.NextStageIdForPersistence,
            source.StageGenerationsForPersistence);
        return cloned;
    }
}
