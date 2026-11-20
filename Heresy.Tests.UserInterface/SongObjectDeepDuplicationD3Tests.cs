using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectDeepDuplicationD3Tests
{
    [Test]
    public void InstrumentDeepFollowsOnlyToneSourcesAndCommitsOneUndoableBatch()
    {
        SongDocument song = new();
        SampleDefinition sample = AddSample(song);
        NodeEnvelopeDefinition envelope =
            new(song.AllocateObjectId(), "Envelope");
        song.Add(envelope);
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        InstrumentDefinition nested =
            new(song.AllocateObjectId(), "Nested");
        nested.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sample.Id, PitchMultiplier = 1.25,
            VolumeEnvelopeId = envelope.Id,
            FilterConfiguration = new FilterInstance(filter),
        });
        song.Add(nested);
        InstrumentDefinition parent = new(song.AllocateObjectId(), "Parent");
        parent.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = nested.Id, VolumeEnvelopeId = envelope.Id,
            FilterConfiguration = new FilterInstance(filter),
        });
        parent.ToneTable.Add(0);
        song.Add(parent);
        ObjectId next = song.NextUnallocatedObjectId;
        uint revision = song.DocumentRevision;

        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, parent.Id);
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(draft.RootCandidate.Id, Is.EqualTo(next));
        Assert.That(draft.Candidates, Has.Count.EqualTo(3));
        Assert.That(draft.IdMap.Keys,
            Is.EquivalentTo(new[] { parent.Id, nested.Id, sample.Id }));
        Assert.That(draft.IdMap.ContainsKey(filter.Id), Is.False);
        Assert.That(draft.IdMap.ContainsKey(envelope.Id), Is.False);

        InstrumentDefinition newParent = (InstrumentDefinition)draft.RootCandidate;
        InstrumentDefinition newNested = (InstrumentDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[nested.Id]);
        SampleDefinition newSample = (SampleDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[sample.Id]);
        Assert.That(newParent.ToneSpecifications[0].SourceId,
            Is.EqualTo(newNested.Id));
        Assert.That(newNested.ToneSpecifications[0].SourceId,
            Is.EqualTo(newSample.Id));
        Assert.That(newParent.ToneSpecifications[0].VolumeEnvelopeId,
            Is.EqualTo(envelope.Id));
        Assert.That(newNested.ToneSpecifications[0].VolumeEnvelopeId,
            Is.EqualTo(envelope.Id));
        Assert.That(newNested.ToneSpecifications[0].FilterConfiguration!.Definition,
            Is.SameAs(filter));
        Assert.That(newSample.PendingAsset, Is.Not.Null);
        Assert.That(newSample.PcmData, Is.SameAs(sample.PcmData));
        Assert.That(parent.ToneSpecifications[0].SourceId, Is.EqualTo(nested.Id));

        SongImportBatchEdit receipt = SongObjectDeepDuplication.Commit(draft);
        Assert.That(receipt.ImportedObjectIds, Has.Count.EqualTo(3));
        Assert.That(song.Objects.Count, Is.EqualTo(8));
        receipt.Undo();
        Assert.That(song.Objects.Count, Is.EqualTo(5));
        receipt.Redo();
        Assert.That(song.Objects[newParent.Id], Is.SameAs(newParent));
        Assert.That(song.Objects[newSample.Id], Is.SameAs(newSample));
    }

    [Test]
    public void SynthGraphDeepClonesRepeatedSourcesAndRebindsCrossPathFiltersAndEnvelopes()
    {
        SongDocument song = new();
        SampleDefinition sample = AddSample(song);
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        NodeEnvelopeDefinition envelope =
            new(song.AllocateObjectId(), "Envelope");
        song.Add(envelope);
        InstrumentDefinition instrument = new(song.AllocateObjectId(), "Instrument");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sample.Id, VolumeEnvelopeId = envelope.Id,
            FilterConfiguration = new FilterInstance(filter),
        });
        song.Add(instrument);
        FmSynthDefinition synth = new(song.AllocateObjectId(), "Synth",
            new FmSynthGraph(
            [
                new FmSoundSourceNode(0, sample.Id, 440),
                new FmSoundSourceNode(1, instrument.Id, 440),
                new FmSoundSourceNode(2, sample.Id, 220), // disconnected
                new FmEnvelopeNode(3, envelope.Id),
                new FmFilterNode(4, 1, new FilterInstance(filter)),
                new FmOperatorNode(5, FmOperatorKind.Add, [0, 3, 4]),
            ], 5));
        synth.NodePositions.Add(new FmSynthNodePosition(2, -50, 30));
        song.Add(synth);

        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, synth.Id);
        Assert.That(draft.Candidates, Has.Count.EqualTo(5));
        Assert.That(draft.IdMap.Keys,
            Is.EquivalentTo(new[] { synth.Id, instrument.Id, sample.Id,
                envelope.Id, filter.Id }));
        FmSynthDefinition newSynth = (FmSynthDefinition)draft.RootCandidate;
        InstrumentDefinition newInstrument = (InstrumentDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[instrument.Id]);
        FilterDefinition newFilter = (FilterDefinition)draft.Candidates
            .Single(item => item.Id == draft.IdMap[filter.Id]);
        FmSoundSourceNode[] sources = newSynth.Graph.Nodes
            .OfType<FmSoundSourceNode>().OrderBy(n => n.Id).ToArray();
        Assert.That(sources, Has.Length.EqualTo(3));
        Assert.That(sources[0].SourceId, Is.EqualTo(draft.IdMap[sample.Id]));
        Assert.That(sources[1].SourceId, Is.EqualTo(newInstrument.Id));
        Assert.That(sources[2].SourceId, Is.EqualTo(draft.IdMap[sample.Id]));
        Assert.That(newSynth.Graph.Nodes.OfType<FmEnvelopeNode>()
            .Single().EnvelopeId, Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(newInstrument.ToneSpecifications[0].SourceId,
            Is.EqualTo(draft.IdMap[sample.Id]));
        // Instrument tone Envelope/Filter references do not trigger traversal;
        // once the Synth reaches them, both still remap to the single clones.
        Assert.That(newInstrument.ToneSpecifications[0].VolumeEnvelopeId,
            Is.EqualTo(draft.IdMap[envelope.Id]));
        Assert.That(newInstrument.ToneSpecifications[0].FilterConfiguration!
            .Definition, Is.SameAs(newFilter));
        Assert.That(newSynth.Graph.Nodes.OfType<FmFilterNode>().Single()
            .Configuration!.Definition, Is.SameAs(newFilter));
        Assert.That(newSynth.NodePositions, Is.EqualTo(synth.NodePositions));
        Assert.That(synth.Graph.Nodes.OfType<FmSoundSourceNode>()
            .First().SourceId, Is.EqualTo(sample.Id));
        Assert.That(instrument.ToneSpecifications[0].FilterConfiguration!
            .Definition, Is.SameAs(filter));

        SongObjectDeepDuplication.Commit(draft);
        Assert.That(song.Objects.Count, Is.EqualTo(10));
    }

    [Test]
    public void SharedDiamondDependencyIsClonedExactlyOnce()
    {
        SongDocument song = new();
        SampleDefinition shared = AddSample(song);
        InstrumentDefinition left = new(song.AllocateObjectId(), "Instrument");
        left.ToneSpecifications.Add(new ToneSpecification { SourceId = shared.Id });
        song.Add(left);
        InstrumentDefinition right = new(song.AllocateObjectId(), "Instrument");
        right.ToneSpecifications.Add(new ToneSpecification { SourceId = shared.Id });
        song.Add(right);
        FmSynthDefinition root = new(song.AllocateObjectId(), "Root",
            new FmSynthGraph([
                new FmSoundSourceNode(0, left.Id, 440),
                new FmSoundSourceNode(1, right.Id, 440),
                new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
            ], 2));
        song.Add(root);

        SongObjectDeepDuplicateDraft plan =
            SongObjectDeepDuplication.Prepare(song, root.Id);
        Assert.That(plan.Candidates, Has.Count.EqualTo(4));
        Assert.That(plan.IdMap.Keys, Is.EquivalentTo(
            new[] { shared.Id, left.Id, right.Id, root.Id }));
        InstrumentDefinition[] instruments = plan.Candidates
            .OfType<InstrumentDefinition>().ToArray();
        Assert.That(instruments, Has.Length.EqualTo(2));
        Assert.That(instruments.Select(i => i.ToneSpecifications.Single().SourceId)
            .Distinct().Single(), Is.EqualTo(plan.IdMap[shared.Id]));
        Assert.That(instruments.Select(i => i.Name),
            Is.EqualTo(new[] { "Instrument 3", "Instrument 4" }));
    }

    [Test]
    public void CrossSynthCycleAndSelfReferenceHaveFiniteClosureAndPreserveTopology()
    {
        SongDocument song = new();
        ObjectId first = song.AllocateObjectId();
        ObjectId second = song.AllocateObjectId();
        FmSynthDefinition a = new(first, "A",
            new FmSynthGraph([new FmSoundSourceNode(0, second, 440)], 0));
        FmSynthDefinition b = new(second, "B",
            new FmSynthGraph([new FmSoundSourceNode(0, first, 440)], 0));
        song.Add(a);
        song.Add(b);
        SongObjectDeepDuplicateDraft plan =
            SongObjectDeepDuplication.Prepare(song, a.Id);
        Assert.That(plan.IdMap, Has.Count.EqualTo(2));
        Assert.That(((FmSoundSourceNode)((FmSynthDefinition)plan.RootCandidate)
            .Graph.Nodes.Single()).SourceId, Is.EqualTo(plan.IdMap[b.Id]));
        FmSynthDefinition clonedB = (FmSynthDefinition)plan.Candidates
            .Single(item => item.Id == plan.IdMap[b.Id]);
        Assert.That(((FmSoundSourceNode)clonedB.Graph.Nodes.Single()).SourceId,
            Is.EqualTo(plan.IdMap[a.Id]));

        SongImportBatchEdit undo = SongObjectDeepDuplication.Commit(plan);
        Assert.That(undo.ImportedObjectIds, Has.Count.EqualTo(2));
        undo.Undo();

        SongDocument selfSong = new();
        ObjectId selfId = selfSong.AllocateObjectId();
        FmSynthDefinition self = new(selfId, "Self",
            new FmSynthGraph([new FmSoundSourceNode(0, selfId, 440)], 0));
        selfSong.Add(self);
        SongObjectDeepDuplicateDraft selfPlan =
            SongObjectDeepDuplication.Prepare(selfSong, selfId);
        Assert.That(selfPlan.IdMap, Has.Count.EqualTo(1));
        Assert.That(((FmSoundSourceNode)((FmSynthDefinition)selfPlan.RootCandidate)
            .Graph.Nodes.Single()).SourceId,
            Is.EqualTo(selfPlan.IdMap[selfId]));
        Assert.That(self.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().SourceId, Is.EqualTo(selfId));
    }

    [Test]
    public void MissingAndIncompatibleTypedEdgesRejectBeforeSongMutation()
    {
        SongDocument song = new();
        InstrumentDefinition missing = new(song.AllocateObjectId(), "Missing");
        missing.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = new ObjectId(12345),
        });
        song.Add(missing);
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        FmSynthDefinition wrong = new(song.AllocateObjectId(), "Wrong",
            new FmSynthGraph([new FmSoundSourceNode(0, filter.Id, 440)], 0));
        song.Add(wrong);
        ObjectId cursor = song.NextUnallocatedObjectId;
        uint revision = song.DocumentRevision;
        Assert.That(() => SongObjectDeepDuplication.Prepare(song, missing.Id),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => SongObjectDeepDuplication.Prepare(song, wrong.Id),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.Objects.Count, Is.EqualTo(3));
    }

    [Test]
    public void ScriptedDependencyIsExplicitlyDeferredUntilD4InsteadOfSilentlyOmitted()
    {
        SongDocument song = new();
        ScriptInstrumentDefinition script =
            new(song.AllocateObjectId(), "Scripted")
            { Source = "_O(4)", PitchExpression = "1.0" };
        song.Add(script);
        FmSynthDefinition root = new(song.AllocateObjectId(), "Root",
            new FmSynthGraph(
                [new FmSoundSourceNode(0, script.Id, 440)], 0));
        song.Add(root);
        ObjectId cursor = song.NextUnallocatedObjectId;
        Assert.That(() => SongObjectDeepDuplication.Prepare(song, root.Id),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => SongObjectDeepDuplication.Prepare(song, script.Id),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.Objects.Count, Is.EqualTo(2));
    }

    [Test]
    public void PostPreparationAudioAssetChangeRefusesEntireDeepBatch()
    {
        using TempDir dir = new();
        string wave = Path.Combine(dir.Path, "sample.wav");
        byte[] audio = Wave(4096, -8192);
        File.WriteAllBytes(wave, audio);
        SongDocument song = new();
        ExternalAssetReference asset = new(wave); // Legacy unhashed sample.
        SampleDefinition sample = new(song.AllocateObjectId(), "Sample", asset);
        sample.ReloadPersistedEncoding(asset, audio);
        song.Add(sample);
        FmSynthDefinition root = new(song.AllocateObjectId(), "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        song.Add(root);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, root.Id);
        ObjectId expected = song.NextUnallocatedObjectId;
        uint before = song.DocumentRevision;
        File.WriteAllBytes(wave, Wave(2048, 1024));
        Assert.That(() => SongObjectDeepDuplication.Commit(draft),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(expected));
        Assert.That(song.DocumentRevision, Is.EqualTo(before));
        Assert.That(song.Objects.Count, Is.EqualTo(2));
    }

    [Test]
    public void StaleAuthoringSnapshotRefusesBatchEvenIfRevisionWasNotMarked()
    {
        SongDocument song = new();
        InstrumentDefinition root = new(song.AllocateObjectId(), "Root");
        root.ToneSpecifications.Add(new ToneSpecification { SourceId = ObjectId.None });
        song.Add(root);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, root.Id);
        root.Offset = 17; // Unmarked change.
        ObjectId cursor = song.NextUnallocatedObjectId;
        Assert.That(() => SongObjectDeepDuplication.Commit(draft),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void CommittedDeepSampleAndSynthSurviveSaveReopen(string extension)
    {
        using TempDir dir = new();
        SongDocument song = new();
        SampleDefinition sample = AddSample(song);
        FmSynthDefinition synth = new(song.AllocateObjectId(), "Synth",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        song.Add(synth);
        SongObjectDeepDuplicateDraft draft =
            SongObjectDeepDuplication.Prepare(song, synth.Id);
        SongObjectDeepDuplication.Commit(draft);
        string path = Path.Combine(dir.Path, "deep" + extension);
        SongDocumentStorage.Save(path, song);
        SongDocument reopened = SongDocumentStorage.Load(path);
        SampleDefinition duplicate = (SampleDefinition)reopened.Objects[
            draft.IdMap[sample.Id]];
        FmSynthDefinition deepSynth = (FmSynthDefinition)reopened.Objects[
            draft.IdMap[synth.Id]];
        Assert.That(duplicate.PcmData, Is.Not.Null);
        Assert.That(duplicate.PcmData!.HasSameAudio(sample.PcmData), Is.True);
        Assert.That(deepSynth.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().SourceId, Is.EqualTo(duplicate.Id));
    }

    private static SampleDefinition AddSample(SongDocument song)
    {
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sample", "sample.wav", Wave(4096, -8192));
        song.Add(sample);
        return sample;
    }

    private static byte[] Wave(short a, short b)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, Encoding.ASCII, leaveOpen: true))
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
            writer.Write(a);
            writer.Write(b);
        }
        return memory.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "heresy-deep-d3-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
