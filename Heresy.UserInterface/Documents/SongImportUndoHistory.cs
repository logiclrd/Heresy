using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A deliberately one-level, document-scoped history for the most recent
/// atomic Synth import. The rest of the editors have independent undo stacks:
/// this is NOT a general song undo history. The Core receipt remains the
/// authority on freshness and refuses intervening authored changes.
/// </summary>
public sealed class SongImportUndoHistory
{
    private SongDocument? _document;
    private SongImportBatchEdit? _receipt;
    private IReadOnlyList<ObjectId> _selectedRoots = [];

    public bool CanUndo(SongDocument document) =>
        ReferenceEquals(_document, document) && _receipt?.IsApplied == true;

    public bool CanRedo(SongDocument document) =>
        ReferenceEquals(_document, document) && _receipt?.IsApplied == false;

    public IReadOnlyList<ObjectId> SelectedRoots(SongDocument document) =>
        ReferenceEquals(_document, document) ? _selectedRoots : [];

    /// <summary>
    /// Register only after a successful commit, never after preparation or
    /// a failed dialog. Another accepted import replaces the previous
    /// one-level receipt; it does not create a falsely replayable stack.
    /// </summary>
    public void Record(SongDocument document, SongImportBatchEdit receipt,
        IReadOnlyList<ObjectId> selectedRoots)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(selectedRoots);
        if (!receipt.BelongsTo(document) || !receipt.IsApplied)
            throw new InvalidOperationException(
                "Only an applied import from this document can enter its undo history.");
        if (selectedRoots.Count == 0 ||
            selectedRoots.Any(id => !receipt.ImportedObjectIds.Contains(id)))
            throw new ArgumentException(
                "Import history requires explicitly selected imported root IDs.",
                nameof(selectedRoots));
        _document = document;
        _receipt = receipt;
        _selectedRoots = Array.AsReadOnly(selectedRoots.ToArray());
    }

    public void Undo(SongDocument document)
    {
        if (!CanUndo(document))
            throw new InvalidOperationException(
                "There is no applied Synth import to undo in this song.");
        _receipt!.Undo();
    }

    public void Redo(SongDocument document)
    {
        if (!CanRedo(document))
            throw new InvalidOperationException(
                "There is no undone Synth import to redo in this song.");
        _receipt!.Redo();
    }

    public void Clear()
    {
        _document = null;
        _receipt = null;
        _selectedRoots = [];
    }
}
