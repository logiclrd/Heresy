using System;
using System.IO;
using System.Linq;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongImportUndoHistoryTests
{
    [Test]
    public void OneImportIsOneUndoRedoUnitWithOriginalIdsAndNoTombstones()
    {
        DocumentWorkspace workspace = new();
        SongDocument song = workspace.Document;
        SongImportBatchEdit receipt = Commit(song, "Root", "Child");
        ObjectId[] roots = [receipt.ImportedObjectIds[0]];
        workspace.ImportUndo.Record(song, receipt, roots);
        Assert.That(workspace.ImportUndo.CanUndo(song), Is.True);
        Assert.That(workspace.ImportUndo.CanRedo(song), Is.False);
        Assert.That(song.Objects, Has.Count.EqualTo(2));

        workspace.ImportUndo.Undo(song);
        Assert.That(song.Objects, Is.Empty);
        Assert.That(song.Tombstones, Is.Empty);
        Assert.That(workspace.ImportUndo.CanUndo(song), Is.False);
        Assert.That(workspace.ImportUndo.CanRedo(song), Is.True);
        Assert.That(workspace.ImportUndo.SelectedRoots(song), Is.EqualTo(roots));

        workspace.ImportUndo.Redo(song);
        Assert.That(song.Objects, Has.Count.EqualTo(2));
        Assert.That(song.Objects.Keys, Is.EquivalentTo(receipt.ImportedObjectIds));
        Assert.That(song.Tombstones, Is.Empty);
        Assert.That(workspace.ImportUndo.CanUndo(song), Is.True);
    }

    [Test]
    public void StaleMarkedAndUnmarkedChangesRefuseWithoutLosingReceipt()
    {
        DocumentWorkspace marked = new();
        SongImportBatchEdit markReceipt = Commit(marked.Document, "Copied");
        marked.ImportUndo.Record(marked.Document, markReceipt,
            [markReceipt.ImportedObjectIds.Single()]);
        marked.Document.MarkChanged(affectsAudio: true);
        Assert.Throws<InvalidOperationException>(
            () => marked.ImportUndo.Undo(marked.Document));
        Assert.That(marked.Document.Objects, Has.Count.EqualTo(1));
        Assert.That(marked.ImportUndo.CanUndo(marked.Document), Is.True,
            "refused undo must not discard the receipt");

        DocumentWorkspace unmarked = new();
        SongImportBatchEdit rawReceipt = Commit(unmarked.Document, "Copied");
        unmarked.ImportUndo.Record(unmarked.Document, rawReceipt,
            [rawReceipt.ImportedObjectIds.Single()]);
        FmSynthDefinition copied = (FmSynthDefinition)unmarked.Document.Objects[
            rawReceipt.ImportedObjectIds.Single()];
        copied.Name = "Unmarked edit";
        Assert.Throws<InvalidOperationException>(
            () => unmarked.ImportUndo.Undo(unmarked.Document));
        Assert.That(copied.Name, Is.EqualTo("Unmarked edit"));
        Assert.That(unmarked.ImportUndo.CanUndo(unmarked.Document), Is.True);
        copied.Name = "Copied";
        unmarked.ImportUndo.Undo(unmarked.Document);
        Assert.That(unmarked.Document.Objects, Is.Empty);
        unmarked.Document.MarkChanged(affectsAudio: false);
        Assert.Throws<InvalidOperationException>(
            () => unmarked.ImportUndo.Redo(unmarked.Document));
        Assert.That(unmarked.ImportUndo.CanRedo(unmarked.Document), Is.True);
        Assert.That(unmarked.Document.Objects, Is.Empty);
    }

    [Test]
    public void ChangedObserverFailureLeavesReceiptAppliedAndRetryWorks()
    {
        DocumentWorkspace workspace = new();
        SongDocument song = workspace.Document;
        SongImportBatchEdit receipt = Commit(song, "Copy");
        workspace.ImportUndo.Record(song, receipt,
            [receipt.ImportedObjectIds.Single()]);
        EventHandler<SongDocumentChangedEventArgs> fail = (_, _) =>
            throw new InvalidOperationException("Injected observer failure");
        // Use the SongDocument's inferred Changed handler type instead of
        // introducing a separate undo notification path.
        song.Changed += fail;
        Assert.Throws<InvalidOperationException>(
            () => workspace.ImportUndo.Undo(song));
        song.Changed -= fail;
        Assert.That(workspace.ImportUndo.CanUndo(song), Is.True);
        Assert.That(song.Objects, Has.Count.EqualTo(1));
        workspace.ImportUndo.Undo(song);
        Assert.That(song.Objects, Is.Empty);
    }

    [Test]
    public void NewOrOpenDiscardsReceiptFromOldDocument()
    {
        DocumentWorkspace workspace = new();
        SongDocument old = workspace.Document;
        SongImportBatchEdit edit = Commit(old, "Copy");
        workspace.ImportUndo.Record(old, edit, [edit.ImportedObjectIds.Single()]);
        workspace.New();
        Assert.That(workspace.ImportUndo.CanUndo(workspace.Document), Is.False);
        Assert.That(workspace.ImportUndo.CanUndo(old), Is.False);
        Assert.Throws<InvalidOperationException>(
            () => workspace.ImportUndo.Undo(workspace.Document));
        Assert.That(old.Objects, Has.Count.EqualTo(1));

        SongImportBatchEdit again = Commit(workspace.Document, "Second");
        workspace.ImportUndo.Record(workspace.Document, again,
            [again.ImportedObjectIds.Single()]);
        string file = Path.Combine(Path.GetTempPath(),
            "heresy-import-undo-" + Guid.NewGuid().ToString("N") + ".hm");
        try
        {
            SongDocumentStorage.Save(file, workspace.Document);
            workspace.Open(file);
            Assert.That(workspace.ImportUndo.CanUndo(workspace.Document), Is.False);
            Assert.That(workspace.ImportUndo.SelectedRoots(workspace.Document),
                Is.Empty);
            Assert.That(workspace.Document.Objects, Has.Count.EqualTo(1));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Test]
    public void RegisterRejectsWrongSongMissingRootAndReplacesPreviousImport()
    {
        SongDocument song = new(), other = new();
        SongImportBatchEdit first = Commit(song, "First");
        SongImportUndoHistory history = new();
        Assert.Throws<InvalidOperationException>(() => history.Record(other,
            first, [first.ImportedObjectIds.Single()]));
        Assert.Throws<ArgumentException>(() => history.Record(song, first,
            [(ObjectId)999U]));
        Assert.That(history.CanUndo(song), Is.False);
        history.Record(song, first, [first.ImportedObjectIds.Single()]);
        SongImportBatchEdit second = Commit(song, "Second");
        history.Record(song, second, [second.ImportedObjectIds.Single()]);
        history.Undo(song);
        Assert.That(song.Objects, Has.Count.EqualTo(1));
        Assert.That(song.Objects.ContainsKey(first.ImportedObjectIds.Single()),
            Is.True);
        Assert.That(history.SelectedRoots(song),
            Is.EqualTo(second.ImportedObjectIds));
        history.Redo(song);
        Assert.That(song.Objects, Has.Count.EqualTo(2));
    }

    private static SongImportBatchEdit Commit(SongDocument song,
        params string[] names)
    {
        ObjectId cursor = song.NextUnallocatedObjectId;
        SongObject[] candidates = names.Select((name, index) =>
            (SongObject)new FmSynthDefinition(
                new ObjectId(cursor.Value + (uint)index), name,
                new FmSynthGraph([new FmConstantNode(0, .5)], 0)))
            .ToArray();
        return song.CommitImportedObjects(candidates, cursor, _ => true);
    }
}
