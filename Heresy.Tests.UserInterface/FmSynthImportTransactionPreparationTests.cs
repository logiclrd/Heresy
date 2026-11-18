using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportTransactionPreparationTests
{
    [Test]
    public void SameSongAndCollidingIdsPrepareDistinctCopiedSourceMultiGraphWithoutMutation()
    {
        SongDocument song = new();
        FmSynthDefinition child = Add(song, "Child",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthDefinition root = Add(song, "Root",
            new FmSynthGraph([
                new FmSoundSourceNode(1, child.Id, 220),
                new FmMultiNode(2, 1),
                new FmSoundSourceNode(3, child.Id, 440),
                new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
                new FmConstantNode(5, 17), // disconnected authored fragment
            ], 4));
        FmImportExactMatchSource source = Discover(song, "same-song.hm", root);
        FmSynthImportPlan plan = Plan(song, source);
        ObjectId cursor = song.NextUnallocatedObjectId;
        int count = song.Objects.Count;
        uint revision = song.DocumentRevision;
        uint audioRevision = song.AudioRevision;

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [source], song);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        Assert.That(prepared.PreparedObjects, Has.Count.EqualTo(2));
        Assert.That(prepared.ProvisionalIds.Values.Distinct().Count(), Is.EqualTo(2));
        Assert.That(prepared.ProvisionalIds.Values.All(id =>
            !song.Objects.ContainsKey(id)), Is.True);

        FmImportObjectKey rootKey = new(source.Discovery.SourceDocumentIdentity, root.Id);
        FmImportObjectKey childKey = new(source.Discovery.SourceDocumentIdentity, child.Id);
        FmSynthDefinition copied = (FmSynthDefinition)prepared.PreparedObjects
            .Single(obj => obj.Source == rootKey).Object;
        Assert.That(copied.Id, Is.EqualTo(prepared.ProvisionalIds[rootKey]));
        Assert.That(prepared.SelectedRootIds, Is.EqualTo([copied.Id]));
        Assert.That(copied.Id, Is.Not.EqualTo(root.Id));
        Assert.That(copied.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Select(node => node.SourceId).Distinct(),
            Is.EqualTo([prepared.ProvisionalIds[childKey]]));
        Assert.That(copied.Graph.Nodes.OfType<FmMultiNode>().Single().InputNodeId,
            Is.EqualTo(1));
        Assert.That(copied.Graph.Nodes.Any(node =>
            node is FmConstantNode { Id: 5 }), Is.True);
        Assert.That(song.Objects.Count, Is.EqualTo(count));
        Assert.That(song.NextUnallocatedObjectId, Is.EqualTo(cursor));
        Assert.That(song.DocumentRevision, Is.EqualTo(revision));
        Assert.That(song.AudioRevision, Is.EqualTo(audioRevision));
    }

    [Test]
    public void PerUseRemapCanReuseLocalWhileAnotherUseCopiesTheSharedChild()
    {
        SongDocument sourceSong = new();
        FmSynthDefinition child = Add(sourceSong, "Child",
            new FmSynthGraph([new FmConstantNode(0, .4)], 0));
        FmSynthDefinition root = Add(sourceSong, "Root",
            new FmSynthGraph([
                new FmSoundSourceNode(1, child.Id, 220),
                new FmSoundSourceNode(2, child.Id, 440),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            ], 3));
        SongDocument localSong = new();
        FmSynthDefinition substitute = Add(localSong, "Local",
            new FmSynthGraph([new FmConstantNode(0, 8)], 0));
        FmImportExactMatchSource source = Discover(sourceSong, "two-uses.hm", root);
        FmSynthImportPlan plan = Plan(localSong, source);
        FmImportReferenceKey first = plan.Effective.References.Single(
            use => use.Use.Owner.SourceObjectId == root.Id
                && use.Use.NodeId == 1 && use.Use.Slot == "Source").Use;
        plan.SetReferenceOverride(first, FmImportMappingKind.ReuseExisting, substitute.Id);

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [source], localSong);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        Assert.That(prepared.PreparedObjects, Has.Count.EqualTo(2));
        FmSynthDefinition newRoot = (FmSynthDefinition)prepared.PreparedObjects.Single(
            obj => obj.Source.SourceObjectId == root.Id).Object;
        Assert.That(newRoot.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single(node => node.Id == 1).SourceId, Is.EqualTo(substitute.Id));
        Assert.That(newRoot.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single(node => node.Id == 2).SourceId,
            Is.EqualTo(prepared.ProvisionalIds[new(source.Discovery.SourceDocumentIdentity, child.Id)]));
        Assert.That(localSong.Objects.Count, Is.EqualTo(1));
    }

    [Test]
    public void CopyInstrumentEnvelopeAndSampleUsesIndependentPendingBytesAndToneRebinding()
    {
        SongDocument original = new();
        byte[] wave = Wave(1000);
        SampleDefinition sample = SampleDefinition.CreateImported(
            original.AllocateObjectId(), "Voice", "voice.wav", wave);
        original.Add(sample);
        NodeEnvelopeDefinition envelope = new(original.AllocateObjectId(), "V");
        original.Add(envelope);
        InstrumentDefinition instrument =
            new(original.AllocateObjectId(), "Instrument");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sample.Id,
            PitchMultiplier = 1.25,
            VolumeEnvelopeId = envelope.Id,
        });
        instrument.ToneTable.Add(0);
        original.Add(instrument);
        FmSynthDefinition root = Add(original, "Parent",
            new FmSynthGraph([new FmSoundSourceNode(0, instrument.Id, 440)], 0));
        SongDocument destination = new();
        FmImportExactMatchSource from = Discover(original, "sample-instrument.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [from], destination);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        Assert.That(prepared.PreparedObjects, Has.Count.EqualTo(4));
        FmSynthDefinition copiedRoot = (FmSynthDefinition)prepared.PreparedObjects
            .Single(x => x.Source.SourceObjectId == root.Id).Object;
        InstrumentDefinition copiedInstrument = (InstrumentDefinition)prepared.PreparedObjects
            .Single(x => x.Source.SourceObjectId == instrument.Id).Object;
        SampleDefinition copiedSample = (SampleDefinition)prepared.PreparedObjects
            .Single(x => x.Source.SourceObjectId == sample.Id).Object;
        NodeEnvelopeDefinition copiedEnvelope = (NodeEnvelopeDefinition)prepared.PreparedObjects
            .Single(x => x.Source.SourceObjectId == envelope.Id).Object;
        Assert.That(copiedRoot.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().SourceId, Is.EqualTo(copiedInstrument.Id));
        Assert.That(copiedInstrument.ToneSpecifications.Single().SourceId,
            Is.EqualTo(copiedSample.Id));
        Assert.That(copiedInstrument.ToneSpecifications.Single().VolumeEnvelopeId,
            Is.EqualTo(copiedEnvelope.Id));
        Assert.That(copiedInstrument.ToneSpecifications.Single().PitchMultiplier,
            Is.EqualTo(1.25));
        Assert.That(copiedSample.PendingAsset, Is.Not.Null);
        Assert.That(copiedSample.Asset, Is.Null);
        Assert.That(copiedSample.PendingAsset!.Bytes.ToArray(), Is.EqualTo(wave));
        Assert.That(destination.Objects, Is.Empty);
        Assert.That(original.Objects.Count, Is.EqualTo(4));
    }

    [Test]
    public void SemanticFilterPortAndPerUseInstanceAreReboundToProvisionalDefinition()
    {
        SongDocument original = new();
        FilterDefinition filter = new(original.AllocateObjectId(), "Filter");
        original.Add(filter);
        FmSynthDefinition root = Add(original, "Filter synth",
            new FmSynthGraph([
                new FmConstantNode(1, 2),
                new FmFilterNode(2, null, new FilterInstance(filter),
                    [new FmFilterParameterConnection(1, FilterKind.OnePole, 0,
                        "CutoffHz", 1)]),
            ], 2));
        FmImportExactMatchSource source = Discover(original, "filter-copy.hm", root);
        SongDocument destination = new();
        FmSynthImportPlan plan = Plan(destination, source);
        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [source], destination);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        FilterDefinition newFilter = (FilterDefinition)prepared.PreparedObjects.Single(
            obj => obj.Source.SourceObjectId == filter.Id).Object;
        FmSynthDefinition newSynth = (FmSynthDefinition)prepared.PreparedObjects.Single(
            obj => obj.Source.SourceObjectId == root.Id).Object;
        FmFilterNode filterNode = newSynth.Graph.Nodes.OfType<FmFilterNode>().Single();
        Assert.That(filterNode.Configuration!.Definition, Is.SameAs(newFilter));
        Assert.That(filterNode.Connections, Has.Count.EqualTo(1));
        Assert.That(filterNode.Connections.Single().SourceNodeId, Is.EqualTo(1));
        Assert.That(newSynth.Graph.Nodes, Has.Count.EqualTo(2));
        Assert.That(destination.Objects, Is.Empty);
    }

    [Test]
    public void ExplicitDisconnectsAndExclusionsProduceValidGraphAndRemoveRoutingHints()
    {
        SongDocument original = new();
        FmSynthDefinition root = Add(original, "Graph",
            new FmSynthGraph([
                new FmConstantNode(1, 1),
                new FmConstantNode(2, 2),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 440,
                    multiplierNodeId: 1),
            ], 3));
        root.ConnectionRoutingHints.Add(new FmSynthConnectionRoutingHint(
            2, 3, 1, []));
        FmImportExactMatchSource source = Discover(original, "disconnect.hm", root);
        FmSynthImportPlan plan = Plan(new SongDocument(), source);
        FmImportGraphInputKey operand = plan.Effective.Connections.Single(x =>
            x.Key.Consumer.NodeId == 3 && x.Key.Source.NodeId == 2).Key;
        plan.SetGraphInputConnected(operand, false);
        FmImportNodeKey excluded = new(plan.SelectedRoots.Single(), 4);
        plan.SetNodeExcluded(excluded, true);

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [source],
                new SongDocument());
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        FmSynthDefinition copied = (FmSynthDefinition)prepared.PreparedObjects.Single().Object;
        Assert.That(copied.Graph.Nodes.OfType<FmOperatorNode>()
            .Single().InputNodeIds, Is.EqualTo([1]));
        Assert.That(copied.Graph.Nodes.Any(x => x.Id == 4), Is.False);
        Assert.That(copied.ConnectionRoutingHints, Is.Empty);
    }

    [Test]
    public void ProvisionalIdsRespectDeletedIdCursorAndDoNotReserveOrCreateTombstones()
    {
        SongDocument original = new();
        FmSynthDefinition root = Add(original, "Root",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        SongDocument destination = new();
        FmSynthDefinition removed = Add(destination, "Removed",
            new FmSynthGraph([new FmConstantNode(0, 0)], 0));
        destination.Remove(removed.Id);
        ObjectId next = destination.NextUnallocatedObjectId;
        int tombstones = destination.Tombstones.Count;
        uint revision = destination.DocumentRevision;
        FmImportExactMatchSource from = Discover(original, "cursor.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);

        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [from], destination);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        Assert.That(prepared.ProvisionalIds.Values.Single(), Is.EqualTo(next));
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(next));
        Assert.That(destination.Tombstones.Count, Is.EqualTo(tombstones));
        Assert.That(destination.DocumentRevision, Is.EqualTo(revision));
    }

    [Test]
    public void FailedStageBReleasesNoCandidateObjectsOrProvisionalIds()
    {
        SongDocument sourceSong = new();
        FmSynthDefinition root = Add(sourceSong, "Root",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        SongDocument destination = new();
        FmImportExactMatchSource from = Discover(sourceSong, "failed.hm", root);
        FmSynthImportPlan plan = Plan(destination, from);
        ObjectId next = destination.NextUnallocatedObjectId;
        root.Graph = new FmSynthGraph([new FmConstantNode(0, .25)], 0);

        FmImportPreparationResult failed =
            FmSynthImportTransactionPreparer.Prepare(plan, [from], destination);
        Assert.That(failed.IsPrepared, Is.False);
        Assert.That(failed.Preflight.Issues, Is.Not.Empty);
        Assert.That(failed.PreparedObjects, Is.Empty);
        Assert.That(failed.ProvisionalIds, Is.Empty);
        Assert.That(failed.SelectedRootIds, Is.Empty);
        Assert.That(destination.NextUnallocatedObjectId, Is.EqualTo(next));
    }

    [Test]
    public void CrossFileSameIdsAreAssignedIndependently()
    {
        SongDocument one = new(), two = new();
        FmSynthDefinition left = Add(one, "Left",
            new FmSynthGraph([new FmConstantNode(0, .1)], 0));
        FmSynthDefinition right = Add(two, "Right",
            new FmSynthGraph([new FmConstantNode(0, .2)], 0));
        Assert.That(left.Id, Is.EqualTo(right.Id));
        FmImportExactMatchSource a = Discover(one, "left-prep.hm", left);
        FmImportExactMatchSource b = Discover(two, "right-prep.hm", right);
        SongDocument destination = new();
        FmSynthImportPlan plan = Plan(destination, a, b);
        FmImportPreparationResult prepared =
            FmSynthImportTransactionPreparer.Prepare(plan, [a, b], destination);
        Assert.That(prepared.IsPrepared, Is.True, Message(prepared));
        Assert.That(prepared.ProvisionalIds, Has.Count.EqualTo(2));
        Assert.That(prepared.SelectedRootIds.Distinct().Count(), Is.EqualTo(2));
        Assert.That(prepared.PreparedObjects.Select(x => x.Object.Name).OrderBy(x => x),
            Is.EqualTo(["Left", "Right"]));
    }

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition definition = new(song.AllocateObjectId(), name, graph);
        song.Add(definition);
        return definition;
    }

    private static FmImportExactMatchSource Discover(SongDocument document,
        string file, FmSynthDefinition root)
    {
        SongFmSynthImportSource source = new(Path.GetFullPath(file), document, [root]);
        return new(source, FmSynthImportGraphDiscovery.Discover(source, [root.Id]));
    }

    private static FmSynthImportPlan Plan(SongDocument destination,
        params FmImportExactMatchSource[] sources)
        => FmSynthImportExactMatcher.CreatePlan(sources, destination);

    private static string Message(FmImportPreparationResult result)
        => string.Join(" | ", result.Preflight.Issues.Select(x => x.Message))
            + " | " + string.Join(" | ", result.Issues.Select(x => x.Message));

    private static byte[] Wave(short sample)
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
            writer.Write(sample);
        }
        return stream.ToArray();
    }
}
