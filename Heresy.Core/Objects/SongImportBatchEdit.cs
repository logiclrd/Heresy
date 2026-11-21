using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.Objects;

/// <summary>
/// Optional placement of a newly committed object relative to an existing
/// organizational tree occurrence. The parent and anchor must belong to the
/// correct fixed section in the destination song at commit time.
/// </summary>
public sealed record SongImportTreePlacement(
    SongTreeFolder Parent, SongTreeNode? InsertAfter = null);

/// <summary>
/// A single guarded undo/redo unit for an atomic imported-object batch.
///
/// It can be placed on a future document-wide undo stack. Until that stack
/// exists, callers may invoke Undo/Redo explicitly. Stale receipts refuse to
/// replace unrelated subsequent song edits, including unmarked authoring
/// changes detectable in the persisted authoring snapshot.
/// </summary>
public sealed class SongImportBatchEdit
{
    internal sealed record Placement(
        SongObject Object, SongTreeFolder Section, SongTreeObject TreeNode,
        SongTreeNode? InsertAfter);

    private readonly SongDocument _document;
    private readonly IReadOnlyList<Placement> _placements;

    internal SongImportBatchEdit(
        SongDocument document, IReadOnlyList<Placement> placements,
        uint initialNextId, uint initialDocumentRevision,
        uint initialAudioRevision, string initialSnapshot,
        string committedSnapshot, uint committedDocumentRevision,
        uint committedAudioRevision)
    {
        _document = document;
        _placements = Array.AsReadOnly(placements.ToArray());
        InitialNextId = initialNextId;
        InitialDocumentRevision = initialDocumentRevision;
        InitialAudioRevision = initialAudioRevision;
        InitialSnapshot = initialSnapshot;
        IsApplied = true;
        ExpectedNextId = document.NextUnallocatedObjectId.Value;
        ExpectedSnapshot = committedSnapshot;
        ExpectedDocumentRevision = committedDocumentRevision;
        ExpectedAudioRevision = committedAudioRevision;
    }

    public bool IsApplied { get; private set; }
    /// <summary>Identity guard for document-scoped UI undo registration.</summary>
    public bool BelongsTo(SongDocument document) =>
        ReferenceEquals(_document, document);

    public IReadOnlyList<ObjectId> ImportedObjectIds =>
        _placements.Select(placement => placement.Object.Id).ToArray();

    public void Undo()
    {
        if (!IsApplied)
            throw new InvalidOperationException("This import has already been undone.");
        _document.ReplayImportBatch(this, apply: false);
    }

    public void Redo()
    {
        if (IsApplied)
            throw new InvalidOperationException("This import is already applied.");
        _document.ReplayImportBatch(this, apply: true);
    }

    internal SongDocument Document => _document;
    internal IReadOnlyList<Placement> Placements => _placements;
    internal uint InitialNextId { get; }
    internal uint InitialDocumentRevision { get; }
    internal uint InitialAudioRevision { get; }
    internal string InitialSnapshot { get; }
    internal uint ExpectedNextId { get; private set; }
    internal uint ExpectedDocumentRevision { get; private set; }
    internal uint ExpectedAudioRevision { get; private set; }
    internal string ExpectedSnapshot { get; private set; }

    internal void Advance(bool applied, uint nextId, uint documentRevision,
        uint audioRevision, string snapshot)
    {
        IsApplied = applied;
        ExpectedNextId = nextId;
        ExpectedDocumentRevision = documentRevision;
        ExpectedAudioRevision = audioRevision;
        ExpectedSnapshot = snapshot;
    }
}
