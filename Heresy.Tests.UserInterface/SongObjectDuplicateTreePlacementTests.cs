using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectDuplicateTreePlacementTests
{
    [Test]
    public void DeepMenuEligibilityMatchesRuntimeSubtypeNotJustSection()
    {
        SongDocument song = new();
        SongObject[] objects =
        [
            new DataSequenceDefinition(new ObjectId(1), "Data sequence"),
            new ScriptSequenceDefinition(new ObjectId(2), "Script sequence"),
            new DataPatternDefinition(new ObjectId(3), "Data pattern"),
            new ScriptPatternDefinition(new ObjectId(4), "Script pattern"),
            new InstrumentDefinition(new ObjectId(5), "Instrument"),
            new ScriptInstrumentDefinition(new ObjectId(6), "Script instrument"),
            new NodeEnvelopeDefinition(new ObjectId(7), "Node envelope"),
            new ScriptEnvelopeDefinition(new ObjectId(8), "Script envelope"),
            new FilterDefinition(new ObjectId(9), "Filter"),
            SampleDefinition.CreateImported(new ObjectId(10), "Sample",
                "sample.wav", Wave()),
            new FmSynthDefinition(new ObjectId(11), "Synth",
                new FmSynthGraph([new FmConstantNode(0, 1.0)], 0)),
        ];
        foreach (SongObject source in objects)
            song.Add(source);

        bool[] shouldOfferDeep =
        [
            false, true, false, true, true, true,
            false, true, false, false, true,
        ];
        for (int index = 0; index < objects.Length; index++)
            Assert.That(SongObjectDuplicateTreePlacement.HasDeepOption(objects[index]),
                Is.EqualTo(shouldOfferDeep[index]),
                $"Wrong deep menu eligibility for {objects[index].GetType().Name}");
    }

    [Test]
    public void ShallowDuplicateUsesExactClickedOccurrenceInNestedFolder()
    {
        SongDocument song = new();
        DataSequenceDefinition source =
            new(song.AllocateObjectId(), "Sequence");
        song.Add(source);
        SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Sequences);
        SongTreeObject first = root.Children.OfType<SongTreeObject>().Single();
        SongTreeFolder folder = SongTreeEditor.CreateFolder(song, root, "Nested");
        SongTreeEditor.MoveInto(song, first, folder);
        SongTreeFolder secondFolder = SongTreeEditor.CreateFolder(song, root, "Other");
        SongTreeObject second = new(source.Name, source.Id);
        secondFolder.Children.Add(second);
        song.MarkChanged(affectsAudio: false);
        ObjectId expectedId = song.NextUnallocatedObjectId;
        uint before = song.DocumentRevision;

        SongObjectDuplicateDraft prepared =
            SongObjectDuplication.Prepare(song, source.Id);
        Assert.That(song.DocumentRevision, Is.EqualTo(before));
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(expectedId));
        SongImportBatchEdit receipt =
            SongObjectDuplication.Commit(prepared, second);
        SongTreeObject placed =
            SongObjectDuplicateTreePlacement.FindCommittedRoot(
                song, expectedId, second);

        Assert.That(secondFolder.Children,
            Is.EqualTo(new SongTreeNode[] { second, placed }));
        Assert.That(placed.Name, Is.EqualTo("Sequence 2"));
        Assert.That(folder.Children, Is.EqualTo(new SongTreeNode[] { first }));
        Assert.That(root.Children.OfType<SongTreeObject>(), Is.Empty);

        receipt.Undo();
        Assert.That(secondFolder.Children, Is.EqualTo(new SongTreeNode[] { second }));
        Assert.That(song.Objects.ContainsKey(expectedId), Is.False);
        receipt.Redo();
        Assert.That(secondFolder.Children, Is.EqualTo(new SongTreeNode[] { second, placed }));
        Assert.That(song.Objects[expectedId], Is.SameAs(prepared.Candidate));
    }

    [Test]
    public void DeepClonesGoToExistingSiblingFoldersAndStayStableAcrossRedo()
    {
        SongDocument song = new();
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sound", "s.wav", Wave());
        song.Add(sample);
        ScriptPatternDefinition pattern = new(song.AllocateObjectId(), "Script")
        {
            Source = $"_O({sample.Id.Value})",
        };
        song.Add(pattern);
        ScriptSequenceDefinition sequence = new(song.AllocateObjectId(), "Root")
        {
            Source = $"_O({pattern.Id.Value})",
        };
        song.Add(sequence);

        SongTreeFolder patternsRoot = song.GetSectionRoot(SongTreeSection.Patterns);
        SongTreeFolder samplesRoot = song.GetSectionRoot(SongTreeSection.Samples);
        SongTreeFolder sequencesRoot = song.GetSectionRoot(SongTreeSection.Sequences);
        SongTreeFolder patternFolder = SongTreeEditor.CreateFolder(song, patternsRoot, "Patterns folder");
        SongTreeFolder sampleFolder = SongTreeEditor.CreateFolder(song, samplesRoot, "Samples folder");
        SongTreeFolder sequenceFolder = SongTreeEditor.CreateFolder(song, sequencesRoot, "Sequences folder");
        SongTreeObject oldPattern = patternsRoot.Children.OfType<SongTreeObject>().Single();
        SongTreeObject oldSample = samplesRoot.Children.OfType<SongTreeObject>().Single();
        SongTreeObject oldSequence = sequencesRoot.Children.OfType<SongTreeObject>().Single();
        SongTreeEditor.MoveInto(song, oldPattern, patternFolder);
        SongTreeEditor.MoveInto(song, oldSample, sampleFolder);
        SongTreeEditor.MoveInto(song, oldSequence, sequenceFolder);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, sequence.Id);
        SongImportBatchEdit edit = SongObjectDeepDuplication.Commit(draft, oldSequence);

        SongTreeObject newSequence = SongObjectDuplicateTreePlacement.FindCommittedRoot(
            song, draft.IdMap[sequence.Id], oldSequence);
        SongTreeObject newPattern = SongObjectDuplicateTreePlacement.FindCommittedRoot(
            song, draft.IdMap[pattern.Id], oldPattern);
        SongTreeObject newSample = SongObjectDuplicateTreePlacement.FindCommittedRoot(
            song, draft.IdMap[sample.Id], oldSample);
        Assert.That(draft.Candidates, Has.Count.EqualTo(3));
        Assert.That(sequenceFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldSequence, newSequence }));
        Assert.That(patternFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldPattern, newPattern }));
        Assert.That(sampleFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldSample, newSample }));
        Assert.That(((ScriptSequenceDefinition)draft.RootCandidate).Source,
            Is.EqualTo($"_O({draft.IdMap[pattern.Id].Value})"));
        Assert.That(((ScriptPatternDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[pattern.Id])).Source,
            Is.EqualTo($"_O({draft.IdMap[sample.Id].Value})"));

        edit.Undo();
        Assert.That(sequenceFolder.Children, Is.EqualTo(new SongTreeNode[] { oldSequence }));
        Assert.That(patternFolder.Children, Is.EqualTo(new SongTreeNode[] { oldPattern }));
        Assert.That(sampleFolder.Children, Is.EqualTo(new SongTreeNode[] { oldSample }));
        edit.Redo();
        Assert.That(sequenceFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldSequence, newSequence }));
        Assert.That(patternFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldPattern, newPattern }));
        Assert.That(sampleFolder.Children,
            Is.EqualTo(new SongTreeNode[] { oldSample, newSample }));
    }

    [Test]
    public void UnplacedDeepDependencyFallsBackToItsSectionRoot()
    {
        SongDocument song = new();
        FilterDefinition referenced = new(song.AllocateObjectId(), "Filter");
        song.Add(referenced);
        ScriptEnvelopeDefinition root =
            new(song.AllocateObjectId(), "Envelope")
            {
                Source = $"_O({referenced.Id.Value})",
            };
        song.Add(root);
        SongTreeFolder filterRoot = song.GetSectionRoot(SongTreeSection.Filters);
        SongTreeObject existing = filterRoot.Children.OfType<SongTreeObject>().Single();
        filterRoot.Children.Remove(existing); // No organizational placement.
        song.MarkChanged(affectsAudio: false);
        SongTreeObject clicked = song.GetSectionRoot(SongTreeSection.Envelopes)
            .Children.OfType<SongTreeObject>().Single();

        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, root.Id);
        SongObjectDeepDuplication.Commit(draft, clicked);
        Assert.That(filterRoot.Children.OfType<SongTreeObject>()
            .Single().ObjectId, Is.EqualTo(draft.IdMap[referenced.Id]));
    }

    [Test]
    public void StaleOrForeignTreePlacementRefusesWithoutPartialPublication()
    {
        SongDocument song = new();
        DataPatternDefinition pattern =
            new(song.AllocateObjectId(), "Pattern");
        song.Add(pattern);
        SongTreeObject original = song.GetSectionRoot(SongTreeSection.Patterns)
            .Children.OfType<SongTreeObject>().Single();
        SongObjectDuplicateDraft draft =
            SongObjectDuplication.Prepare(song, pattern.Id);
        SongTreeFolder unowned = new("Detached");
        SongTreeObject invalid = new("Fake", pattern.Id);
        unowned.Children.Add(invalid);
        ObjectId cursor = song.NextUnallocatedObjectId;
        uint before = song.DocumentRevision;
        Assert.That(() => SongObjectDuplication.Commit(draft, invalid),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.DocumentRevision, Is.EqualTo(before));
        Assert.That(song.Objects.Count, Is.EqualTo(1));

        Assert.That(() => song.CommitImportedObjects(
            [draft.Candidate], cursor, _ => true,
            [new SongImportTreePlacement(
                song.GetSectionRoot(SongTreeSection.Sequences), null)]),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
        Assert.That(original.ObjectId, Is.EqualTo(pattern.Id));
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void ScriptDeepDuplicateWithSiblingPlacementPersistsBothFormats(string suffix)
    {
        using TempDir temp = new();
        SongDocument song = new();
        ScriptSequenceDefinition script = new(song.AllocateObjectId(), "Script")
        {
            Source = "_O(1);",
        };
        song.Add(script);
        SongTreeObject original = song.GetSectionRoot(SongTreeSection.Sequences)
            .Children.OfType<SongTreeObject>().Single();
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, script.Id);
        SongObjectDeepDuplication.Commit(draft, original);
        string path = Path.Combine(temp.Path, "duplication" + suffix);
        SongDocumentStorage.Save(path, song);
        SongDocument reopened = SongDocumentStorage.Load(path);
        ScriptSequenceDefinition cloned =
            (ScriptSequenceDefinition)reopened.Objects[draft.RootCandidate.Id];
        Assert.That(cloned.Source,
            Is.EqualTo($"_O({cloned.Id.Value});"));
        SongTreeObject[] nodes = reopened
            .GetSectionRoot(SongTreeSection.Sequences)
            .Children.OfType<SongTreeObject>().ToArray();
        Assert.That(nodes.Select(item => item.ObjectId),
            Is.EqualTo(new[] { script.Id, cloned.Id }));
    }

    private static byte[] Wave()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(40);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(4);
            writer.Write((short)4096);
            writer.Write((short)-8192);
        }
        return stream.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "heresy-dup-placement-"
            + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
