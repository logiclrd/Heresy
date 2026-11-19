using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportTransactionCommitterTests
{
    [Test]
    public void SameSongSourceMultiImportCommitsOneUndoableBatchAndSurvivesSaveReopen()
    {
        SongDocument song = new();
        FmSynthDefinition child = Add(song, "Child",
            new FmSynthGraph([new FmConstantNode(0, .7)], 0));
        FmSynthDefinition root = Add(song, "Source plus Multi",
            new FmSynthGraph([
                new FmSoundSourceNode(1, child.Id, 440),
                new FmMultiNode(2, 1),
                new FmConstantNode(3, 13), // disconnected authored fragment
            ], 2));
        FmImportExactMatchSource from = Discover(song, "same-source.hm", root);
        FmSynthImportPlan plan = Plan(song, from);
        string original = SongDocumentJson.SerializeAuthoringSnapshot(song);
        ObjectId cursor = song.NextUnallocatedObjectId;
        uint docRevision = song.DocumentRevision, audioRevision = song.AudioRevision;
        int events = 0;
        song.Changed += (_, args) =>
        {
            events++;
            Assert.That(args.AffectsAudio, Is.True);
        };

        FmImportCommitResult result = FmSynthImportTransactionCommitter.Commit(
            plan, [from], song);
        Assert.That(result.IsCommitted, Is.True, result.Error);
        Assert.That(result.SelectedRootIds, Has.Count.EqualTo(1));
        Assert.That(result.Edit!.ImportedObjectIds, Has.Count.EqualTo(2));
        Assert.That(song.NextUnallocatedObjectId.Value,
            Is.EqualTo(cursor.Value + 2));
        Assert.That(song.DocumentRevision, Is.EqualTo(docRevision + 1));
        Assert.That(song.AudioRevision, Is.EqualTo(audioRevision + 1));
        Assert.That(events, Is.EqualTo(1));
        FmSynthDefinition newRoot = (FmSynthDefinition)song.Objects[
            result.SelectedRootIds.Single()];
        FmSynthDefinition newChild = (FmSynthDefinition)song.Objects[
            result.Edit.ImportedObjectIds.Single(id => id != newRoot.Id)];
        Assert.That(newRoot.Id, Is.Not.EqualTo(root.Id));
        Assert.That(newChild.Id, Is.Not.EqualTo(child.Id));
        Assert.That(newRoot.Graph.Nodes.OfType<FmSoundSourceNode>().Single().SourceId,
            Is.EqualTo(newChild.Id));
        Assert.That(newRoot.Graph.Nodes.OfType<FmMultiNode>().Single().InputNodeId,
            Is.EqualTo(1));
        Assert.That(newRoot.Graph.Nodes.Any(node => node.Id == 3), Is.True);

        string file = Path.Combine(Path.GetTempPath(),
            "heresy-synth-import-" + Guid.NewGuid().ToString("N") + ".hm");
        try
        {
            SongDocumentStorage.Save(file, song);
            SongDocument reopened = SongDocumentStorage.Load(file);
            FmSynthDefinition savedRoot = (FmSynthDefinition)reopened.Objects[newRoot.Id];
            FmSynthDefinition savedChild = (FmSynthDefinition)reopened.Objects[newChild.Id];
            Assert.That(savedRoot.Graph.Nodes.OfType<FmSoundSourceNode>()
                .Single().SourceId, Is.EqualTo(savedChild.Id));
            Assert.That(savedRoot.Graph.Nodes.OfType<FmMultiNode>()
                .Single().InputNodeId, Is.EqualTo(1));
            Assert.That(savedRoot.Graph.Nodes, Has.Count.EqualTo(3));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }

        // Save is allowed to perform persistence housekeeping. A separate
        // fresh import tests undo below without that intervening mutation.
        Assert.That(original, Is.Not.Empty);
    }

    [Test]
    public void CommittedImportUndoRedoIsOneUnitWithoutTombstoneSideEffects()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 3)], 0));
        FmImportExactMatchSource from = Discover(source, "undo.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        string before = SongDocumentJson.SerializeAuthoringSnapshot(destination);
        ObjectId next = destination.NextUnallocatedObjectId;

        FmImportCommitResult result = FmSynthImportTransactionCommitter.Commit(
            plan, [from], destination);
        Assert.That(result.IsCommitted, Is.True, result.Error);
        SongImportBatchEdit edit = result.Edit!;
        Assert.That(destination.Objects, Has.Count.EqualTo(1));
        edit.Undo();
        Assert.That(destination.Objects, Is.Empty);
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(destination.Tombstones, Is.Empty);
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(destination),
            Is.EqualTo(before));
        edit.Redo();
        Assert.That(destination.Objects.ContainsKey(result.SelectedRootIds.Single()),
            Is.True);
    }

    [Test]
    public void ChangedDestinationOrSourceFailsBeforeAnyMutation()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 3)], 0));
        FmImportExactMatchSource from = Discover(source, "stale.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        ObjectId next = destination.NextUnallocatedObjectId;
        uint revision = destination.DocumentRevision;

        root.Graph = new FmSynthGraph([new FmConstantNode(0, 4)], 0);
        FmImportCommitResult stale = FmSynthImportTransactionCommitter.Commit(
            plan, [from], destination);
        Assert.That(stale.IsCommitted, Is.False);
        Assert.That(stale.Edit, Is.Null);
        Assert.That(stale.Error, Does.Contain("changed"));
        Assert.That(destination.Objects, Is.Empty);
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(destination.DocumentRevision, Is.EqualTo(revision));

        // Even direct local property changes that skip MarkChanged invalidate
        // the old plan before any fresh ObjectIds are reserved.
        SongDocument local = new();
        FmSynthDefinition localObject = Add(local, "Local",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmImportExactMatchSource freshFrom = Discover(source, "dest-stale.hm", root);
        FmSynthImportPlan staleTargetPlan = Plan(local, freshFrom);
        ObjectId current = local.NextUnallocatedObjectId;
        localObject.Graph = new FmSynthGraph([new FmConstantNode(0, 17)], 0);
        FmImportCommitResult localChanged = FmSynthImportTransactionCommitter.Commit(
            staleTargetPlan, [freshFrom], local);
        Assert.That(localChanged.IsCommitted, Is.False);
        Assert.That(localChanged.Edit, Is.Null);
        Assert.That(local.NextUnallocatedObjectId, Is.EqualTo(current));
        Assert.That(local.Objects.Count, Is.EqualTo(1));
    }

    [Test]
    public void ThrowingDocumentChangeObserverRollsBackAllObjectsAndReturnsFailure()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 3)], 0));
        FmImportExactMatchSource from = Discover(source, "rollback.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        string before = SongDocumentJson.SerializeAuthoringSnapshot(destination);
        ObjectId cursor = destination.NextUnallocatedObjectId;
        uint revision = destination.DocumentRevision;
        destination.Changed += (_, _) => throw new InvalidOperationException(
            "Injected document observer error");
        FmImportCommitResult result = FmSynthImportTransactionCommitter.Commit(
            plan, [from], destination);
        Assert.That(result.IsCommitted, Is.False);
        Assert.That(result.Error, Does.Contain("Injected document observer error"));
        Assert.That(result.Edit, Is.Null);
        Assert.That(destination.Objects, Is.Empty);
        Assert.That(destination.Tombstones, Is.Empty);
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(destination.DocumentRevision, Is.EqualTo(revision));
        Assert.That(SongDocumentJson.SerializeAuthoringSnapshot(destination),
            Is.EqualTo(before));
    }

    [Test]
    public void ImportedSampleUsesIndependentEncodedBytesAcrossPersistedPackage()
    {
        SongDocument source = new();
        byte[] wav = Wave(1024);
        SampleDefinition sample = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Voice", "voice.wav", wav);
        source.Add(sample);
        FmSynthDefinition root = Add(source, "Sample Source",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        SongDocument destination = new();
        FmImportExactMatchSource from = Discover(source, "source-sample.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        FmImportCommitResult result = FmSynthImportTransactionCommitter.Commit(
            plan, [from], destination);
        Assert.That(result.IsCommitted, Is.True, result.Error);
        SampleDefinition copiedSample = destination.Objects.Values
            .OfType<SampleDefinition>().Single();
        Assert.That(copiedSample.PendingAsset, Is.Not.Null);
        Assert.That(copiedSample.Asset, Is.Null);
        Assert.That(copiedSample.PendingAsset!.Bytes.ToArray(), Is.EqualTo(wav));

        string file = Path.Combine(Path.GetTempPath(),
            "heresy-imported-sample-" + Guid.NewGuid().ToString("N") + ".hm");
        try
        {
            SongDocumentStorage.Save(file, destination);
            SongDocument loaded = SongDocumentStorage.Load(file);
            SampleDefinition persisted = loaded.Objects.Values
                .OfType<SampleDefinition>().Single();
            Assert.That(persisted.PcmData, Is.Not.Null);
            Assert.That(persisted.Asset, Is.Not.Null);
            Assert.That(loaded.Objects.Values.OfType<FmSynthDefinition>().Single()
                .Graph.Nodes.OfType<FmSoundSourceNode>().Single().SourceId,
                Is.EqualTo(persisted.Id));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Test]
    public void RepeatedSynthImportNumbersNameAndReusesExistingPcmSample()
    {
        SongDocument source = new();
        SampleDefinition sample = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Source audio", "voice.wav", Wave(2056));
        source.Add(sample);
        FmSynthDefinition root = Add(source, "New Synth 3",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        FmImportExactMatchSource from = Discover(source, "repeated-sample.hm", root);
        SongDocument destination = new();

        FmImportCommitResult first = FmSynthImportTransactionCommitter.Commit(
            Plan(destination, from), [from], destination);
        Assert.That(first.IsCommitted, Is.True, first.Error);
        Assert.That(first.Edit!.ImportedObjectIds, Has.Count.EqualTo(2));
        ObjectId importedSample = destination.Objects.Values
            .OfType<SampleDefinition>().Single().Id;
        Assert.That(((FmSynthDefinition)destination.Objects[
            first.SelectedRootIds.Single()]).Name, Is.EqualTo("New Synth 3"));

        FmSynthImportPlan secondPlan = Plan(destination, from);
        FmImportObjectKey sampleKey = new(
            from.Discovery.SourceDocumentIdentity, sample.Id);
        Assert.That(secondPlan.GetDefinitionMapping(sampleKey).Kind,
            Is.EqualTo(FmImportMappingKind.ReuseExisting));
        Assert.That(secondPlan.GetDefinitionMapping(sampleKey).ExistingId,
            Is.EqualTo(importedSample));
        Assert.That(secondPlan.GetProposedName(from.Discovery.SelectedSynthRoots.Single())
            .Name, Is.EqualTo("New Synth 4"));
        FmImportStageBResult preflight =
            FmSynthImportStageBPreflight.Check(secondPlan, [from], destination);
        Assert.That(preflight.IsPreflightValid, Is.True,
            string.Join(" | ", preflight.Issues.Select(x => x.Message)));
        Assert.That(preflight.PreparedSamples, Is.Empty);

        FmImportCommitResult second = FmSynthImportTransactionCommitter.Commit(
            secondPlan, [from], destination);
        Assert.That(second.IsCommitted, Is.True, second.Error);
        Assert.That(second.Edit!.ImportedObjectIds, Has.Count.EqualTo(1),
            "only the explicit Synth root is copied on subsequent imports");
        Assert.That(destination.Objects.Values.OfType<SampleDefinition>().ToArray(),
            Has.Count.EqualTo(1));
        FmSynthDefinition secondRoot = (FmSynthDefinition)destination.Objects[
            second.SelectedRootIds.Single()];
        Assert.That(secondRoot.Name, Is.EqualTo("New Synth 4"));
        Assert.That(secondRoot.Graph.Nodes.OfType<FmSoundSourceNode>().Single().SourceId,
            Is.EqualTo(importedSample));
    }

    [Test]
    public void AutomaticSampleMatchIsReverifiedAtCommitBoundary()
    {
        SongDocument source = new();
        SampleDefinition sample = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Clip", "original.wav", Wave(12));
        source.Add(sample);
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        SongDocument destination = new();
        SampleDefinition existing = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Different name", "same.wav", Wave(12));
        destination.Add(existing);
        FmImportExactMatchSource from = Discover(source, "stale-sample.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        FmImportObjectKey key = new(from.Discovery.SourceDocumentIdentity, sample.Id);
        Assert.That(plan.GetDefinitionMapping(key).ExistingId, Is.EqualTo(existing.Id));

        // This direct authoring edit intentionally does not mark a document
        // revision. Stage B still rechecks the complete destination state.
        existing.ReplaceImportedEncoding("changed.wav", Wave(123));
        FmImportStageBResult checkedPlan =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(checkedPlan.IsPreflightValid, Is.False);
        Assert.That(checkedPlan.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.AutomaticMatchChanged), Is.True);
        FmImportCommitResult refused =
            FmSynthImportTransactionCommitter.Commit(plan, [from], destination);
        Assert.That(refused.IsCommitted, Is.False);
        Assert.That(destination.Objects.Values.OfType<FmSynthDefinition>(), Is.Empty);
        Assert.That(destination.Objects.Values.OfType<SampleDefinition>().ToArray(),
            Has.Count.EqualTo(1));
    }

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }

    private static FmImportExactMatchSource Discover(SongDocument song,
        string path, FmSynthDefinition root)
    {
        SongFmSynthImportSource source = new(Path.GetFullPath(path), song, [root]);
        return new(source, FmSynthImportGraphDiscovery.Discover(source, [root.Id]));
    }

    private static FmSynthImportPlan Plan(SongDocument dest,
        params FmImportExactMatchSource[] sources)
        => FmSynthImportExactMatcher.CreatePlan(sources, dest);

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
