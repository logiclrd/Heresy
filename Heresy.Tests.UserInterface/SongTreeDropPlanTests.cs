using System;
using System.IO;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongTreeDropPlanTests
{
    [Test]
    public void FolderRowsOfferBeforeIntoAndAfterZones()
    {
        SongTreeFolder folder = new("Destination");
        Assert.That(SongTreeDropPlan.ForRow(folder, 0.01).Placement,
            Is.EqualTo(SongTreeDropPlacement.Before));
        Assert.That(SongTreeDropPlan.ForRow(folder, 0.25).Placement,
            Is.EqualTo(SongTreeDropPlacement.Into));
        Assert.That(SongTreeDropPlan.ForRow(folder, 0.50).Placement,
            Is.EqualTo(SongTreeDropPlacement.Into));
        Assert.That(SongTreeDropPlan.ForRow(folder, 0.75).Placement,
            Is.EqualTo(SongTreeDropPlacement.After));
    }

    [Test]
    public void ObjectRowsOfferBeforeAndAfterZones()
    {
        SongTreeObject item = new("Object", new ObjectId(17));
        Assert.That(SongTreeDropPlan.ForRow(item, 0.49).Placement,
            Is.EqualTo(SongTreeDropPlacement.Before));
        Assert.That(SongTreeDropPlan.ForRow(item, 0.50).Placement,
            Is.EqualTo(SongTreeDropPlacement.After));
    }

    [Test]
    public void DragIntoEmptyFolderAndBackToInvisibleSectionRootInAllSixPanes()
    {
        foreach (SongTreeSection section in SongTreeSections.DocumentOrder)
        {
            SongDocument song = new();
            SongTreeFolder root = song.GetSectionRoot(section);
            SongTreeFolder first = SongTreeEditor.CreateFolder(song, root, "First");
            SongTreeFolder target = SongTreeEditor.CreateFolder(song, root, "Empty");
            uint audioRevision = song.AudioRevision;

            SongTreeDropPlan into = SongTreeDropPlan.ForRow(target, 0.50);
            Assert.That(into.CanApply(song, first), Is.True, section.ToString());
            into.Apply(song, first);
            Assert.That(target.Children, Is.EqualTo(new SongTreeNode[] { first }));
            Assert.That(song.AudioRevision, Is.EqualTo(audioRevision));

            SongTreeDropPlan outToRoot = SongTreeDropPlan.ForSectionRoot(root);
            Assert.That(outToRoot.CanApply(song, first), Is.True);
            outToRoot.Apply(song, first);
            Assert.That(root.Children, Is.EqualTo(new SongTreeNode[] { target, first }));
            Assert.That(target.Children, Is.Empty);
            Assert.That(song.AudioRevision, Is.EqualTo(audioRevision));
        }
    }

    [Test]
    public void BeforeAndAfterDropReorderSiblingsInBothDirectionsWithoutAudioRevision()
    {
        SongDocument song = new();
        SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeFolder first = SongTreeEditor.CreateFolder(song, root, "First");
        SongTreeFolder second = SongTreeEditor.CreateFolder(song, root, "Second");
        SongTreeFolder third = SongTreeEditor.CreateFolder(song, root, "Third");
        uint audio = song.AudioRevision;

        SongTreeDropPlan.ForRow(third, 0.90).Apply(song, first);
        Assert.That(root.Children, Is.EqualTo(new SongTreeNode[] { second, third, first }));

        SongTreeDropPlan.ForRow(second, 0.10).Apply(song, first);
        Assert.That(root.Children, Is.EqualTo(new SongTreeNode[] { first, second, third }));

        uint revision = song.DocumentRevision;
        SongTreeDropPlan.ForRow(second, 0.10).Apply(song, first);
        Assert.That(song.DocumentRevision, Is.EqualTo(revision),
            "Repeating an already-satisfied sibling reorder is a no-op");
        Assert.That(song.AudioRevision, Is.EqualTo(audio));
    }

    [Test]
    public void NestedFolderDropAndRootDropPreserveMusicalObjectIdAndName()
    {
        SongDocument song = new();
        DataPatternDefinition pattern = new(song.AllocateObjectId(), "Note pattern");
        song.Add(pattern);
        SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeObject original = root.Children.OfType<SongTreeObject>().Single();
        SongTreeFolder parent = SongTreeEditor.CreateFolder(song, root, "A");
        SongTreeFolder nested = SongTreeEditor.CreateFolder(song, parent, "B");
        uint audio = song.AudioRevision;

        SongTreeDropPlan.ForRow(nested, 0.5).Apply(song, original);
        Assert.That(nested.Children.Single(), Is.SameAs(original));
        Assert.That(original.ObjectId, Is.EqualTo(pattern.Id));
        Assert.That(song.Objects[pattern.Id], Is.SameAs(pattern));

        SongTreeDropPlan.ForSectionRoot(root).Apply(song, original);
        Assert.That(root.Children.Last(), Is.SameAs(original));
        Assert.That(original.ObjectId, Is.EqualTo(pattern.Id));
        Assert.That(original.Name, Is.EqualTo(pattern.Name));
        Assert.That(song.AudioRevision, Is.EqualTo(audio));
    }

    [Test]
    public void DropRejectsCrossSectionAndRecursiveFolderMovement()
    {
        SongDocument song = new();
        SongTreeFolder patterns = song.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeFolder parent = SongTreeEditor.CreateFolder(song, patterns, "Parent");
        SongTreeFolder child = SongTreeEditor.CreateFolder(song, parent, "Child");
        SongTreeFolder samples = song.GetSectionRoot(SongTreeSection.Samples);
        uint revision = song.DocumentRevision;

        Assert.That(SongTreeDropPlan.ForRow(child, 0.5).CanApply(song, parent),
            Is.False, "A folder must not become its own descendant");
        Assert.That(SongTreeDropPlan.ForSectionRoot(samples).CanApply(song, child),
            Is.False, "Cross-pane organization is forbidden");
        Assert.That(SongTreeDropPlan.ForRow(parent, 0.1).CanApply(song, parent),
            Is.False, "A row cannot be inserted before itself");
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void ReorganizedFoldersAndObjectPlacementPersistInBothFormats(string extension)
    {
        SongDocument song = new();
        DataPatternDefinition pattern = new(song.AllocateObjectId(), "Persist");
        song.Add(pattern);
        SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeObject item = root.Children.OfType<SongTreeObject>().Single();
        SongTreeFolder folder = SongTreeEditor.CreateFolder(song, root, "Moved");
        SongTreeDropPlan.ForRow(folder, 0.5).Apply(song, item);
        Assert.That(folder.Children.Single(), Is.SameAs(item));

        string file = Path.Combine(Path.GetTempPath(),
            "heresy-tree-drag-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            SongDocumentStorage.Save(file, song);
            SongDocument reloaded = SongDocumentStorage.Load(file);
            SongTreeFolder loadedRoot = reloaded.GetSectionRoot(SongTreeSection.Patterns);
            SongTreeFolder loadedFolder = loadedRoot.Children.OfType<SongTreeFolder>()
                .Single(x => x.Name == "Moved");
            Assert.That(loadedFolder.Children.OfType<SongTreeObject>()
                .Single().ObjectId, Is.EqualTo(pattern.Id));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
