using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectCreationFolderTests
{
    [Test]
    public void AllNonAudioCreationActionsPlaceInTheirSelectedSectionFolders()
    {
        DocumentWorkspace workspace = new();
        SongDocument document = workspace.Document;

        (SongTreeSection Section, Func<SongTreeFolder, SongObject> Create)[] actions =
        [
            (SongTreeSection.Sequences,
                folder => SequenceDocumentEditor.CreateDataSequence(
                    workspace, "Sequence", destinationFolder: folder)),
            (SongTreeSection.Sequences,
                folder => ScriptDocumentEditor.CreateScriptSequence(
                    workspace, "Script sequence", destinationFolder: folder)),
            (SongTreeSection.Patterns,
                folder => PatternDocumentEditor.CreateDataPattern(
                    workspace, "Pattern", destinationFolder: folder)),
            (SongTreeSection.Patterns,
                folder => ScriptDocumentEditor.CreateScriptPattern(
                    workspace, "Script pattern", destinationFolder: folder)),
            (SongTreeSection.Instruments,
                folder => InstrumentDocumentEditor.CreateInstrument(
                    workspace, "Instrument", destinationFolder: folder)),
            (SongTreeSection.Instruments,
                folder => ScriptInstrumentDocumentEditor.Create(
                    workspace, "Script instrument", destinationFolder: folder)),
            (SongTreeSection.Filters,
                folder => FilterDocumentEditor.CreateFilter(
                    workspace, "Filter", destinationFolder: folder)),
            (SongTreeSection.Envelopes,
                folder => EnvelopeDocumentEditor.CreateNodeEnvelope(
                    workspace, "Node envelope", destinationFolder: folder)),
            (SongTreeSection.Envelopes,
                folder => EnvelopeDocumentEditor.CreateScriptEnvelope(
                    workspace, "Script envelope", destinationFolder: folder)),
            (SongTreeSection.Samples,
                folder => FmSynthDocumentEditor.CreateFmSynth(
                    workspace, "Synth", destinationFolder: folder)),
        ];

        foreach ((SongTreeSection section, Func<SongTreeFolder, SongObject> create)
            in actions)
        {
            SongTreeFolder root = document.GetSectionRoot(section);
            SongTreeFolder outer = root.Children.OfType<SongTreeFolder>()
                .FirstOrDefault(folder => folder.Name == "Outer")
                ?? SongTreeEditor.CreateFolder(document, root, "Outer");
            SongTreeFolder inner = outer.Children.OfType<SongTreeFolder>()
                .FirstOrDefault(folder => folder.Name == "Inner")
                ?? SongTreeEditor.CreateFolder(document, outer, "Inner");
            int previousCount = inner.Children.Count;
            SongObject added = create(inner);

            Assert.That(document.Objects[added.Id], Is.SameAs(added));
            Assert.That(inner.Children.Count, Is.EqualTo(previousCount + 1));
            Assert.That(((SongTreeObject)inner.Children.Last()).ObjectId,
                Is.EqualTo(added.Id));
            Assert.That(root.Children.OfType<SongTreeObject>(), Is.Empty);
        }
    }

    [Test]
    public void FolderResolutionUsesSelectedOccurrenceAndIgnoresOtherSections()
    {
        DocumentWorkspace workspace = new();
        SongDocument document = workspace.Document;
        SongTreeFolder root = document.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeFolder first = SongTreeEditor.CreateFolder(document, root, "First");
        SongTreeFolder nested = SongTreeEditor.CreateFolder(document, first, "Nested");
        SongTreeFolder second = SongTreeEditor.CreateFolder(document, root, "Second");
        SongObject pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Shared", destinationFolder: nested);
        SongTreeObject original = (SongTreeObject)nested.Children.Single();
        SongTreeObject secondOccurrence = new(pattern.Name, pattern.Id);
        second.Children.Add(secondOccurrence);

        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns, nested), Is.SameAs(nested));
        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns, original), Is.SameAs(nested));
        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns, secondOccurrence), Is.SameAs(second));
        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns,
            document.GetSectionRoot(SongTreeSection.Filters)), Is.SameAs(root));
        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns, null), Is.SameAs(root));
        Assert.That(SongTreeEditor.ResolveCreationFolder(
            document, SongTreeSection.Patterns, root), Is.SameAs(root));
    }

    [Test]
    public void DetachedAndCrossSectionDestinationsRejectWithoutAllocatingIds()
    {
        DocumentWorkspace workspace = new();
        SongDocument document = workspace.Document;
        SongTreeFolder wrongSection = document.GetSectionRoot(SongTreeSection.Filters);
        SongTreeFolder detached = new("Detached");
        ObjectId cursor = document.NextUnallocatedObjectId;
        uint revision = document.DocumentRevision;
        uint audio = document.AudioRevision;

        foreach (SongTreeFolder invalid in new[] { wrongSection, detached, document.Root })
        {
            Assert.Throws<InvalidOperationException>(() =>
                ScriptDocumentEditor.CreateScriptSequence(
                    workspace, "Invalid", destinationFolder: invalid));
            Assert.Throws<InvalidOperationException>(() =>
                document.Add(new Heresy.Core.Sequences.DataSequenceDefinition(
                    cursor, "Invalid"), destinationFolder: invalid));
            Assert.That(document.NextUnallocatedObjectId, Is.EqualTo(cursor));
            Assert.That(document.Objects, Is.Empty);
            Assert.That(document.DocumentRevision, Is.EqualTo(revision));
            Assert.That(document.AudioRevision, Is.EqualTo(audio));
        }

        Assert.Throws<InvalidOperationException>(() =>
            SongTreeEditor.ResolveCreationFolder(
                document, SongTreeSection.Sequences, detached));
    }

    [Test]
    public void ImportedAudioFromFileAndSongUsesSelectedFolderInSourceOrder()
    {
        DocumentWorkspace workspace = new();
        SongTreeFolder root = workspace.Document.GetSectionRoot(SongTreeSection.Samples);
        SongTreeFolder target = SongTreeEditor.CreateFolder(workspace.Document,
            root, "Selected");
        string assetFile = Path.Combine(Path.GetTempPath(),
            "heresy-folder-import-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.WriteAllBytes(assetFile, Wave(12));
            SampleDefinition importedFile = SampleDocumentEditor.Import(workspace,
                assetFile, destinationFolder: target);

            SongDocument sourceDocument = new();
            SampleDefinition first = SampleDefinition.CreateImported(
                sourceDocument.AllocateObjectId(), "First", "first.wav", Wave(5));
            sourceDocument.Add(first);
            SampleDefinition second = SampleDefinition.CreateImported(
                sourceDocument.AllocateObjectId(), "Second", "second.wav", Wave(6));
            sourceDocument.Add(second);
            SongSampleImportSource source = new("source.hm", sourceDocument,
                [first, second]);

            SampleDefinition[] copied = SampleDocumentEditor.ImportFromSong(
                workspace, source, [second.Id, first.Id], target).ToArray();

            Assert.That(copied.Select(x => x.Name),
                Is.EqualTo(new[] { "First", "Second" }));
            Assert.That(target.Children.OfType<SongTreeObject>()
                .Select(node => node.ObjectId),
                Is.EqualTo(new[] { importedFile.Id, copied[0].Id, copied[1].Id }));
            Assert.That(root.Children.OfType<SongTreeObject>(), Is.Empty);

            ObjectId expectedCursor = workspace.Document.NextUnallocatedObjectId;
            Assert.Throws<InvalidOperationException>(() =>
                SampleDocumentEditor.ImportFromSong(
                    workspace, source, [first.Id, second.Id],
                    workspace.Document.GetSectionRoot(SongTreeSection.Filters)));
            Assert.That(workspace.Document.NextUnallocatedObjectId,
                Is.EqualTo(expectedCursor));
            Assert.That(target.Children.Count, Is.EqualTo(3));
        }
        finally
        {
            if (File.Exists(assetFile))
                File.Delete(assetFile);
        }
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void NewlyCreatedObjectsPreserveNestedPlacementAcrossPersistence(string extension)
    {
        DocumentWorkspace workspace = new();
        SongDocument document = workspace.Document;
        SongTreeFolder patterns = document.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeFolder outer = SongTreeEditor.CreateFolder(document, patterns, "Outer");
        SongTreeFolder inner = SongTreeEditor.CreateFolder(document, outer, "Inner");
        SongObject pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Nested pattern", destinationFolder: inner);

        string path = Path.Combine(Path.GetTempPath(),
            "heresy-folder-create-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            SongDocumentStorage.Save(path, document);
            SongDocument reopened = SongDocumentStorage.Load(path);
            SongTreeFolder loadedOuter = reopened
                .GetSectionRoot(SongTreeSection.Patterns)
                .Children.OfType<SongTreeFolder>().Single();
            SongTreeFolder loadedInner = loadedOuter.Children
                .OfType<SongTreeFolder>().Single();
            Assert.That(loadedInner.Children.OfType<SongTreeObject>()
                .Single().ObjectId, Is.EqualTo(pattern.Id));
            Assert.That(loadedInner.Name, Is.EqualTo("Inner"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static byte[] Wave(short value)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(38);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(2);
            writer.Write(value);
        }
        return stream.ToArray();
    }
}
