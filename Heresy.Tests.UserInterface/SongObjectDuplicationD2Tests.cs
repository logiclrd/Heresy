using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
public sealed class SongObjectDuplicationD2Tests
{
    [Test]
    public void PendingSampleCreatesIndependentEncodedAssetButReusesImmutablePcm()
    {
        SongDocument song = new();
        byte[] wave = Wave(4096, -4096);
        SampleDefinition source = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Source", "source.wav", wave);
        source.Loop = new SampleLoop(SampleLoopMode.PingPong, 0, 2);
        source.SourceChannelPositions.Add(new Vector3(.1f, .2f, -.3f));
        song.Add(source);

        ObjectId beforeId = song.NextUnallocatedObjectId;
        uint beforeRevision = song.DocumentRevision;
        SongObjectDuplicateDraft draft = SongObjectDuplication.Prepare(song, source.Id);
        SampleDefinition copy = (SampleDefinition)draft.Candidate;

        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(beforeId));
        Assert.That(song.DocumentRevision, Is.EqualTo(beforeRevision));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
        Assert.That(copy.Id, Is.EqualTo(beforeId));
        Assert.That(copy.Name, Is.EqualTo("Source 2"));
        Assert.That(copy.PendingAsset, Is.Not.Null);
        Assert.That(copy.PendingAsset, Is.Not.SameAs(source.PendingAsset));
        Assert.That(copy.PendingAsset!.Bytes.Span.SequenceEqual(
            source.PendingAsset!.Bytes.Span), Is.True);
        Assert.That(copy.Asset, Is.Null);
        Assert.That(copy.PcmData, Is.SameAs(source.PcmData));
        Assert.That(copy.Loop, Is.EqualTo(source.Loop));
        Assert.That(copy.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
        Assert.That(copy.SourceChannelPositions,
            Is.EqualTo(source.SourceChannelPositions));
        copy.SourceChannelPositions.Clear();
        copy.Loop = new SampleLoop(SampleLoopMode.None, 0, 0);
        Assert.That(source.SourceChannelPositions, Has.Count.EqualTo(1));
        Assert.That(source.Loop.Mode, Is.EqualTo(SampleLoopMode.PingPong));

        SongImportBatchEdit receipt = SongObjectDuplication.Commit(draft);
        Assert.That(receipt.ImportedObjectIds.Single(), Is.EqualTo(copy.Id));
        Assert.That(song.Objects.Count, Is.EqualTo(2));
        receipt.Undo();
        Assert.That(song.Objects.Count, Is.EqualTo(1));
        receipt.Redo();
        Assert.That(song.Objects[copy.Id], Is.SameAs(copy));
    }

    [Test]
    public void DataInstrumentClonesOwnedTonesAndFilterOverridesButNotDependencies()
    {
        SongDocument song = new();
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        NodeEnvelopeDefinition envelope = new(song.AllocateObjectId(), "Envelope");
        song.Add(envelope);
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sample", "sample.wav", Wave(400, 800));
        song.Add(sample);
        InstrumentDefinition source = new(song.AllocateObjectId(), "Instrument")
        {
            Divisions = 19,
            Offset = -7,
            NnaOverride = InstrumentNnaOverride.UseToneNna,
            NnaFadeDuration = TimeSpan.FromMilliseconds(140),
        };
        source.ToneSpecifications.Add(CreateTone(sample.Id, envelope.Id, filter));
        source.ToneTable.AddRange([0, -1, 0]);
        song.Add(source);

        InstrumentDefinition copy = (InstrumentDefinition)
            SongObjectDuplication.Prepare(song, source.Id).Candidate;
        Assert.That(copy.Id, Is.Not.EqualTo(source.Id));
        Assert.That(copy.Name, Is.EqualTo("Instrument 2"));
        Assert.That(copy.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
        Assert.That(copy.Divisions, Is.EqualTo(19));
        Assert.That(copy.Offset, Is.EqualTo(-7));
        Assert.That(copy.NnaOverride, Is.EqualTo(source.NnaOverride));
        Assert.That(copy.NnaFadeDuration, Is.EqualTo(source.NnaFadeDuration));
        Assert.That(copy.ToneTable, Is.EqualTo(source.ToneTable));
        Assert.That(copy.ToneSpecifications, Has.Count.EqualTo(1));
        AssertSeparateTone(source.ToneSpecifications[0], copy.ToneSpecifications[0]);
        copy.ToneSpecifications.Clear();
        copy.ToneTable[0] = -1;
        Assert.That(source.ToneSpecifications, Has.Count.EqualTo(1));
        Assert.That(source.ToneTable[0], Is.Zero);
    }

    [Test]
    public void ScriptInstrumentPreservesAllThreeExpressionsAndIndependentTones()
    {
        SongDocument song = new();
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sample", "sample.wav", Wave(400, 800));
        song.Add(sample);
        ScriptInstrumentDefinition source = new(song.AllocateObjectId(), "Scripted")
        {
            Source = "_O(2);",
            PitchExpression = "1.25",
            NnaExpression = "InstrumentNnaAction.Continue",
            NnaFadeDuration = TimeSpan.FromMilliseconds(225),
        };
        source.ToneSpecifications.Add(CreateTone(sample.Id, null, filter));
        song.Add(source);

        ScriptInstrumentDefinition copy = (ScriptInstrumentDefinition)
            SongObjectDuplication.Prepare(song, source.Id).Candidate;
        Assert.That(copy.Source, Is.EqualTo("_O(2);"));
        Assert.That(copy.PitchExpression, Is.EqualTo("1.25"));
        Assert.That(copy.NnaExpression, Is.EqualTo(source.NnaExpression));
        Assert.That(copy.NnaFadeDuration, Is.EqualTo(source.NnaFadeDuration));
        Assert.That(copy.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
        AssertSeparateTone(source.ToneSpecifications[0], copy.ToneSpecifications[0]);
        copy.Source = "0";
        copy.ToneSpecifications.Clear();
        Assert.That(source.Source, Is.EqualTo("_O(2);"));
        Assert.That(source.ToneSpecifications, Has.Count.EqualTo(1));
    }

    [Test]
    public void SynthCopiesAllGraphNodesIncludingDisconnectedLayoutAndFilterState()
    {
        SongDocument song = new();
        FilterDefinition filter = new(song.AllocateObjectId(), "Filter");
        song.Add(filter);
        NodeEnvelopeDefinition envelope = new(song.AllocateObjectId(), "Envelope");
        song.Add(envelope);
        SampleDefinition sample = SampleDefinition.CreateImported(
            song.AllocateObjectId(), "Sample", "sample.wav", Wave(1024, -2048));
        song.Add(sample);
        FilterInstance configured = ConfiguredUse(filter);
        FmSynthDefinition source = new(song.AllocateObjectId(), "Synth",
            new FmSynthGraph(
            [
                new FmConstantNode(0, .25),
                new FmNoiseNode(1, -2.0, 2.0), // Disconnected
                new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
                    220, curvature: .8),
                new FmSoundSourceNode(3, sample.Id, 440),
                new FmEnvelopeNode(4, envelope.Id),
                new FmMultiNode(5, 3, voiceCount: 3),
                new FmOperatorNode(6, FmOperatorKind.Add, [2, 5, 4]),
                new FmFilterNode(7, 6, configured,
                    [new FmFilterParameterConnection(
                        1, FilterKind.OnePole, 0, "__stale__", 0)],
                    tailSeconds: .25),
            ], 7));
        source.NodePositions.Add(new FmSynthNodePosition(7, -140, 900));
        source.ConnectionRoutingHints.Add(new FmSynthConnectionRoutingHint(
            6, 7, 0, [new FmSynthRoutePoint(-55, 72)]));
        source.FilterBatchChangesNodeIds.Add(7);
        song.Add(source);
        SongObjectDuplicateDraft draft = SongObjectDuplication.Prepare(song, source.Id);
        FmSynthDefinition copy = (FmSynthDefinition)draft.Candidate;

        Assert.That(copy.Name, Is.EqualTo("Synth 2"));
        Assert.That(copy.Graph, Is.Not.SameAs(source.Graph));
        Assert.That(copy.Graph.OutputNodeId, Is.EqualTo(source.Graph.OutputNodeId));
        Assert.That(copy.Graph.Nodes, Has.Count.EqualTo(8));
        for (int i = 0; i < copy.Graph.Nodes.Count; i++)
        {
            Assert.That(copy.Graph.Nodes[i].Id, Is.EqualTo(source.Graph.Nodes[i].Id));
            Assert.That(copy.Graph.Nodes[i], Is.Not.SameAs(source.Graph.Nodes[i]));
        }
        Assert.That(((FmSoundSourceNode)copy.Graph.Nodes[3]).SourceId,
            Is.EqualTo(sample.Id));
        Assert.That(((FmEnvelopeNode)copy.Graph.Nodes[4]).EnvelopeId,
            Is.EqualTo(envelope.Id));
        Assert.That(copy.Graph.Nodes.OfType<FmNoiseNode>().Count(), Is.EqualTo(1));
        Assert.That(copy.SourceColourArgb, Is.EqualTo(source.SourceColourArgb));
        Assert.That(copy.NodePositions, Is.EqualTo(source.NodePositions));
        Assert.That(copy.ConnectionRoutingHints, Has.Count.EqualTo(1));
        Assert.That(copy.ConnectionRoutingHints[0],
            Is.Not.SameAs(source.ConnectionRoutingHints[0]));
        Assert.That(copy.ConnectionRoutingHints[0].RoutePoints,
            Is.EqualTo(source.ConnectionRoutingHints[0].RoutePoints));
        Assert.That(copy.FilterBatchChangesNodeIds,
            Is.EquivalentTo(source.FilterBatchChangesNodeIds));
        FmFilterNode sourceFilter = (FmFilterNode)source.Graph.Nodes[7];
        FmFilterNode copyFilter = (FmFilterNode)copy.Graph.Nodes[7];
        Assert.That(copyFilter.Configuration, Is.Not.SameAs(sourceFilter.Configuration));
        Assert.That(copyFilter.Configuration!.Definition, Is.SameAs(filter));
        Assert.That(copyFilter.Connections, Is.EqualTo(sourceFilter.Connections));
        Assert.That(copyFilter.Connections[0], Is.Not.SameAs(sourceFilter.Connections[0]));
        copyFilter.Configuration.ReplaceStageOverrides(1, []);
        Assert.That(sourceFilter.Configuration!.GetOverrides(1), Is.Not.Empty);
        copy.NodePositions.Clear();
        copy.FilterBatchChangesNodeIds.Clear();
        Assert.That(source.NodePositions, Has.Count.EqualTo(1));
        Assert.That(source.FilterBatchChangesNodeIds, Has.Count.EqualTo(1));
        SongObjectDuplication.Commit(draft);
        Assert.That(song.Objects.Values.OfType<FmSynthDefinition>().Count(),
            Is.EqualTo(2));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SourceFileChangedAfterPrepareRejectsAtCommitWithoutPublishing(bool encodedHash)
    {
        using TempDir temp = new();
        string file = Path.Combine(temp.Path, "sound.wav");
        byte[] original = Wave(800, 1200);
        File.WriteAllBytes(file, original);
        ExternalAssetReference reference = encodedHash
            ? ExternalAssetIntegrity.CreateReference(file)
            : new ExternalAssetReference(file);
        SongDocument song = new();
        SampleDefinition sample = new(song.AllocateObjectId(), "Sound", reference);
        sample.ReloadPersistedEncoding(reference, original);
        song.Add(sample);
        SongObjectDuplicateDraft draft = SongObjectDuplication.Prepare(song, sample.Id);

        File.WriteAllBytes(file, Wave(1700, 2300));
        ObjectId next = song.NextUnallocatedObjectId;
        uint revision = song.DocumentRevision;
        Assert.That(() => SongObjectDuplication.Commit(draft),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
    }

    [Test]
    public void ChangedHashedSourceFileRejectsBeforePrepareWithoutMutation()
    {
        using TempDir temp = new();
        string file = Path.Combine(temp.Path, "sound.wav");
        byte[] original = Wave(800, 1200);
        File.WriteAllBytes(file, original);
        ExternalAssetReference reference = ExternalAssetIntegrity.CreateReference(file);
        SongDocument song = new();
        SampleDefinition sample = new(song.AllocateObjectId(), "Sound", reference);
        sample.ReloadPersistedEncoding(reference, original);
        song.Add(sample);
        File.WriteAllBytes(file, Wave(400, 500));

        ObjectId next = song.NextUnallocatedObjectId;
        uint revision = song.DocumentRevision;
        Assert.That(() => SongObjectDuplication.Prepare(song, sample.Id),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.Objects.Count, Is.EqualTo(1));
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void DuplicatedPersistedSampleCanSaveReopenAndPlayItsOriginalPcm(string extension)
    {
        using TempDir temp = new();
        string sourceFile = Path.Combine(temp.Path, "sample.wav");
        byte[] original = Wave(12288, -8192);
        File.WriteAllBytes(sourceFile, original);
        ExternalAssetReference reference = ExternalAssetIntegrity.CreateReference(sourceFile);
        SongDocument song = new();
        SampleDefinition source = new(song.AllocateObjectId(), "Sample", reference);
        source.ReloadPersistedEncoding(reference, original);
        source.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 2);
        song.Add(source);

        SongObjectDuplicateDraft draft = SongObjectDuplication.Prepare(song, source.Id);
        SampleDefinition copy = (SampleDefinition)draft.Candidate;
        SongObjectDuplication.Commit(draft);
        Assert.That(copy.PendingAsset, Is.Not.Null);
        Assert.That(copy.PcmData, Is.SameAs(source.PcmData));

        string file = Path.Combine(temp.Path, "duplicates"+extension);
        SongDocumentStorage.Save(file, song);
        SongDocument reopened = SongDocumentStorage.Load(file);
        SampleDefinition[] samples = reopened.Objects.Values
            .OfType<SampleDefinition>().OrderBy(s => s.Id.Value).ToArray();
        Assert.That(samples, Has.Length.EqualTo(2));
        Assert.That(samples.Select(s => s.Name),
            Is.EqualTo(new[] { "Sample", "Sample 2" }));
        foreach (SampleDefinition sample in samples)
        {
            Assert.That(sample.PcmData, Is.Not.Null);
            Assert.That(sample.PcmData!.HasSameAudio(source.PcmData), Is.True);
            Assert.That(sample.PcmData.GetSample(0, 0),
                Is.EqualTo(source.PcmData!.GetSample(0, 0)));
            Assert.That(sample.Loop, Is.EqualTo(source.Loop));
        }
    }

    private static ToneSpecification CreateTone(
        ObjectId source, ObjectId? envelope, FilterDefinition filter)
        => new()
        {
            SourceId = source,
            PitchMultiplier = 1.5,
            VolumeEnvelopeId = envelope,
            PitchEnvelopeId = envelope,
            PanningEnvelopeId = envelope,
            FilterEnvelopeId = envelope,
            FilterConfiguration = ConfiguredUse(filter),
        };

    private static FilterInstance ConfiguredUse(FilterDefinition filter)
    {
        FilterInstance configuration = new(filter);
        KeyValuePair<string, FilterParameterValue> first =
            filter.Stages[0].DefaultParameters.First();
        configuration.SetStageParameter(1, first.Key, first.Value);
        return configuration;
    }

    private static void AssertSeparateTone(ToneSpecification source, ToneSpecification copy)
    {
        Assert.That(copy, Is.Not.SameAs(source));
        Assert.That(copy.SourceId, Is.EqualTo(source.SourceId));
        Assert.That(copy.PitchMultiplier, Is.EqualTo(source.PitchMultiplier));
        Assert.That(copy.Nna, Is.EqualTo(source.Nna));
        Assert.That(copy.VolumeEnvelopeId, Is.EqualTo(source.VolumeEnvelopeId));
        Assert.That(copy.PitchEnvelopeId, Is.EqualTo(source.PitchEnvelopeId));
        Assert.That(copy.PanningEnvelopeId, Is.EqualTo(source.PanningEnvelopeId));
        Assert.That(copy.FilterEnvelopeId, Is.EqualTo(source.FilterEnvelopeId));
        Assert.That(copy.FilterConfiguration, Is.Not.Null);
        Assert.That(copy.FilterConfiguration, Is.Not.SameAs(source.FilterConfiguration));
        Assert.That(copy.FilterConfiguration!.Definition,
            Is.SameAs(source.FilterConfiguration!.Definition));
        Assert.That(copy.FilterConfiguration.GetOverrides(1),
            Is.EquivalentTo(source.FilterConfiguration.GetOverrides(1)));
        copy.FilterConfiguration.ReplaceStageOverrides(1, []);
        Assert.That(source.FilterConfiguration.GetOverrides(1), Is.Not.Empty);
    }

    private static byte[] Wave(short first, short second)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(40); // 36 bytes overhead plus 4 bytes (two 16-bit mono frames)
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
            writer.Write(first);
            writer.Write(second);
        }
        return memory.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "heresy-dup-d2-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
