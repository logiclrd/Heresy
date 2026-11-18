using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongDocumentAuthoringSnapshotTests
{
    [Test]
    public void SnapshotDoesNotPruneUnreferencedTombstonesOrAdvanceRevisions()
    {
        SongDocument song = new();
        FmSynthDefinition removed = new(song.AllocateObjectId(), "Removed",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        song.Add(removed);
        song.Remove(removed.Id);
        Assert.That(song.Tombstones.ContainsKey(removed.Id), Is.True);
        uint documentRevision = song.DocumentRevision;
        uint audioRevision = song.AudioRevision;

        string snapshot = SongDocumentJson.SerializeAuthoringSnapshot(song);
        Assert.That(snapshot, Does.Contain("\"tombstones\""));
        Assert.That(snapshot, Does.Contain("\"lastKnownName\""));
        Assert.That(snapshot, Does.Contain("\"Removed\""));
        Assert.That(song.Tombstones.ContainsKey(removed.Id), Is.True);
        Assert.That(song.DocumentRevision, Is.EqualTo(documentRevision));
        Assert.That(song.AudioRevision, Is.EqualTo(audioRevision));
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(song),
            Is.EqualTo(snapshot));
    }

    [Test]
    public void SnapshotObservesUnmarkedParameterEditsWithoutChangingRevision()
    {
        SongDocument song = new();
        FmSynthDefinition root = new(song.AllocateObjectId(), "Tone",
            new FmSynthGraph([new FmConstantNode(0, .1)], 0));
        song.Add(root);
        uint revision = song.DocumentRevision;
        string before = SongDocumentJson.SerializeAuthoringSnapshot(song);
        root.Graph = new FmSynthGraph([new FmConstantNode(0, .9)], 0);
        string after = SongDocumentJson.SerializeAuthoringSnapshot(song);
        Assert.That(after, Is.Not.EqualTo(before));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
    }
}
