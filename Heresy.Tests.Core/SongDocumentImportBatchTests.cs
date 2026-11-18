using System;
using System.Linq;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongDocumentImportBatchTests
{
    [Test]
    public void CommitPublishesAllObjectsAndOneEventUndoRedoEachRestoreOneConsistentState()
    {
        SongDocument destination = new();
        FmSynthDefinition original = New(destination.AllocateObjectId(), "Existing");
        destination.Add(original);
        ObjectId cursor = destination.NextUnallocatedObjectId;
        uint revision = destination.DocumentRevision;
        uint audio = destination.AudioRevision;
        string before = SongDocumentJson.SerializeAuthoringSnapshot(destination);
        FmSynthDefinition first = New(cursor, "First");
        FmSynthDefinition second = New(new ObjectId(cursor.Value + 1), "Second");
        int events = 0;
        destination.Changed += (_, args) =>
        {
            events++;
            Assert.That(destination.Objects.ContainsKey(first.Id), Is.EqualTo(
                destination.Objects.ContainsKey(second.Id)),
                "event observers must never see a partially installed batch");
            Assert.That(args.AffectsAudio, Is.True);
        };

        SongImportBatchEdit edit = destination.CommitImportedObjects(
            [first, second], cursor, _ => true);
        Assert.That(events, Is.EqualTo(1));
        Assert.That(destination.DocumentRevision, Is.EqualTo(revision + 1));
        Assert.That(destination.AudioRevision, Is.EqualTo(audio + 1));
        Assert.That(destination.NextUnallocatedObjectId.Value,
            Is.EqualTo(cursor.Value + 2));
        Assert.That(edit.IsApplied, Is.True);
        Assert.That(edit.ImportedObjectIds, Is.EqualTo(new[] { first.Id, second.Id }));
        Assert.That(destination.GetSectionRoot(SongTreeSection.Samples).Children
            .OfType<SongTreeObject>().Select(x => x.ObjectId),
            Does.Contain(first.Id));
        Assert.That(destination.Objects[first.Id], Is.SameAs(first));
        Assert.That(destination.Objects[second.Id], Is.SameAs(second));

        edit.Undo();
        Assert.That(events, Is.EqualTo(2));
        Assert.That(edit.IsApplied, Is.False);
        Assert.That(destination.Objects.Count, Is.EqualTo(1));
        Assert.That(destination.Tombstones, Is.Empty);
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(destination),
            Is.EqualTo(before));

        edit.Redo();
        Assert.That(events, Is.EqualTo(3));
        Assert.That(edit.IsApplied, Is.True);
        Assert.That(destination.Objects[first.Id], Is.SameAs(first));
        Assert.That(destination.Objects[second.Id], Is.SameAs(second));
        Assert.That(destination.NextUnallocatedObjectId.Value,
            Is.EqualTo(cursor.Value + 2));
    }

    [Test]
    public void ThrowingChangeHandlerRollsBackEntireCommitIncludingRevisionsAndTree()
    {
        SongDocument document = new();
        ObjectId next = document.NextUnallocatedObjectId;
        string before = SongDocumentJson.SerializeAuthoringSnapshot(document);
        uint revision = document.DocumentRevision, audio = document.AudioRevision;
        document.Changed += (_, _) => throw new InvalidOperationException("Injected handler failure");

        Assert.That(() => document.CommitImportedObjects(
            [New(next, "Alpha"), New(new ObjectId(next.Value + 1), "Beta")],
            next, _ => true),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Injected"));
        Assert.That(document.Objects, Is.Empty);
        Assert.That(document.Tombstones, Is.Empty);
        Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(document.DocumentRevision, Is.EqualTo(revision));
        Assert.That(document.AudioRevision, Is.EqualTo(audio));
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(document),
            Is.EqualTo(before));
    }

    [Test]
    public void CallbackFailureAndChangedIdCursorPreventEvenFirstPublication()
    {
        SongDocument document = new();
        ObjectId next = document.NextUnallocatedObjectId;
        FmSynthDefinition newSource = New(next, "New");
        int events = 0;
        document.Changed += (_, _) => events++;

        Assert.That(() => document.CommitImportedObjects([newSource], next,
            _ => false), Throws.TypeOf<InvalidOperationException>());
        Assert.That(document.Objects, Is.Empty);
        Assert.That(events, Is.Zero);
        Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));

        document.AllocateObjectId();
        Assert.That(() => document.CommitImportedObjects([newSource], next,
            _ => true), Throws.TypeOf<InvalidOperationException>());
        Assert.That(document.Objects, Is.Empty);
        Assert.That(events, Is.Zero);
    }

    [Test]
    public void DuplicateNoncontiguousAndOccupiedCandidateIdsFailWithoutDocumentEdit()
    {
        SongDocument document = new();
        ObjectId next = document.NextUnallocatedObjectId;
        uint revision = document.DocumentRevision;
        Assert.That(() => document.CommitImportedObjects(
            [New(next, "One"), New(next, "Duplicate")],
            next, _ => true), Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => document.CommitImportedObjects(
            [New(new ObjectId(next.Value + 1), "OutOfOrder")],
            next, _ => true), Throws.TypeOf<InvalidOperationException>());
        Assert.That(document.Objects, Is.Empty);
        Assert.That(document.DocumentRevision, Is.EqualTo(revision));
        Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(next));
    }

    [Test]
    public void UndoRefusesLaterMarkedOrUnmarkedEdits()
    {
        SongDocument document = new();
        ObjectId next = document.NextUnallocatedObjectId;
        FmSynthDefinition imported = New(next, "Imported");
        SongImportBatchEdit edit = document.CommitImportedObjects(
            [imported], next, _ => true);
        imported.Name = "Unmarked edit";
        Assert.That(() => edit.Undo(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(edit.IsApplied, Is.True);
        Assert.That(document.Objects.ContainsKey(next), Is.True);
        imported.Name = "Imported";
        document.MarkChanged(affectsAudio: false);
        Assert.That(() => edit.Undo(), Throws.TypeOf<InvalidOperationException>());
        Assert.That(document.Objects.ContainsKey(next), Is.True);
    }

    [Test]
    public void ThrowingUndoChangeHandlerLeavesBatchAppliedAndRetainsUndoReceipt()
    {
        SongDocument document = new();
        ObjectId next = document.NextUnallocatedObjectId;
        SongImportBatchEdit edit = document.CommitImportedObjects(
            [New(next, "A"), New(new ObjectId(next.Value + 1), "B")],
            next, _ => true);
        string committed = SongDocumentJson.SerializeAuthoringSnapshot(document);
        uint revision = document.DocumentRevision, audio = document.AudioRevision;
        document.Changed += (_, _) => throw new InvalidOperationException("Undo observer failure");
        Assert.That(() => edit.Undo(),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Undo observer"));
        Assert.That(edit.IsApplied, Is.True);
        Assert.That(document.Objects.Count, Is.EqualTo(2));
        Assert.That(document.DocumentRevision, Is.EqualTo(revision));
        Assert.That(document.AudioRevision, Is.EqualTo(audio));
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(document),
            Is.EqualTo(committed));
    }

    private static FmSynthDefinition New(ObjectId id, string name)
        => new(id, name, new FmSynthGraph([new FmConstantNode(0, 1)], 0));
}
