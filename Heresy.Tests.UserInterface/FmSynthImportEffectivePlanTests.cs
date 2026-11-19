using System;
using System.IO;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportEffectivePlanTests
{
    [Test]
    public void FilterSwapDropsIncompatibleModulationButKeepsAudioAndSharedConsumer()
    {
        SongDocument source = new();
        FilterDefinition original = Filter(source.AllocateObjectId(),
            FilterKind.OnePole);
        source.Add(original);
        FmFilterNode stage = FilterNode(4, original, audio: 3, modulator: 1);
        FmSynthDefinition synth = Add(source, "Filtered", new FmSynthGraph([
            stage, // Discover Filter's input use before the other consumer.
            new FmConstantNode(1, 0.5),
            new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440),
            new FmOperatorNode(3, FmOperatorKind.Add, [0, 1]),
        ], 4));

        FilterDefinition other = Filter((ObjectId)200U, FilterKind.SvfNotch);
        FmImportDiscoveredGraph discovery = Discover(source, synth);
        FmSynthImportPlan plan = new([discovery], destinationFilters: [other]);
        FmImportNodeKey modulator = new(discovery.SelectedSynthRoots[0], 1);
        FmImportEntityKey entity = FmImportEntityKey.ForNode(modulator);
        FmImportGraphInputKey cutoff = discovery.Connections
            .Where(x => x.Target.NodeId == 4 && x.PortIdentity.Contains("CutoffHz"))
            .Select(FmImportGraphInputKey.From).Single();
        FmImportGraphInputKey audio = discovery.Connections
            .Where(x => x.Target.NodeId == 4 && x.PortIdentity == "audio")
            .Select(FmImportGraphInputKey.From).Single();
        FmImportGraphInputKey secondary = discovery.Connections
            .Where(x => x.Target.NodeId == 3 && x.Source.NodeId == 1)
            .Select(FmImportGraphInputKey.From).Single();
        FmImportObjectKey filterKey = new(discovery.SourceDocumentIdentity, original.Id);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(modulator, cutoff)));

        uint beforeSource = source.DocumentRevision;
        plan.SetDefinitionMapping(filterKey, FmImportMappingKind.ReuseExisting, other.Id);
        Assert.That(plan.Effective.ConnectionByKey[cutoff].IsActive, Is.False);
        Assert.That(plan.Effective.ConnectionByKey[cutoff].InactiveReason,
            Does.Contain("no matching"));
        Assert.That(plan.Effective.ConnectionByKey[audio].IsActive, Is.True);
        Assert.That(plan.Effective.ConnectionByKey[audio].DestinationPortIndex, Is.EqualTo(0));
        Assert.That(plan.Effective.ConnectionByKey[secondary].IsActive, Is.True);
        Assert.That(plan.Effective.NodeByKey[modulator].IsIncluded, Is.True);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(modulator, secondary)),
            "another consumer must become canonical when a Filter port is lost");

        plan.SetDefinitionMapping(filterKey, FmImportMappingKind.ImportCopy);
        Assert.That(plan.Effective.ConnectionByKey[cutoff].IsActive, Is.True);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(modulator, secondary)),
            "restoring the Filter port must not steal back canonical ownership");
        Assert.That(source.DocumentRevision, Is.EqualTo(beforeSource));
    }

    [Test]
    public void FilterSemanticRebindingFollowsStageIdentityNotPortOrdinal()
    {
        SongDocument source = new();
        FilterDefinition original = new(source.AllocateObjectId(), "two stages",
            [new FilterStageDefinition(1, "first", FilterKind.OnePole),
             new FilterStageDefinition(2, "second", FilterKind.SvfLowPass)]);
        source.Add(original);
        FmSynthDefinition synth = Add(source, "A", new FmSynthGraph([
            new FmConstantNode(1, 0.5),
            FilterNode(4, original, audio: 1, modulator: 1),
        ], 4));
        FilterDefinition reversed = new((ObjectId)201U, "reversed",
            [new FilterStageDefinition(2, "second", FilterKind.SvfLowPass),
             new FilterStageDefinition(1, "first", FilterKind.OnePole)]);
        FmImportDiscoveredGraph graph = Discover(source, synth);
        FmSynthImportPlan plan = new([graph], destinationFilters: [reversed]);
        FmImportGraphInputKey cutoff = graph.Connections
            .Where(x => x.PortIdentity.EndsWith(":CutoffHz")
                && x.PortIdentity.Contains("filter:1:"))
            .Select(FmImportGraphInputKey.From).Single();
        int sourceIndex = graph.Connections.Single(x =>
            FmImportGraphInputKey.From(x) == cutoff).TargetInputIndex;
        FmImportObjectKey filter = new(graph.SourceDocumentIdentity, original.Id);

        plan.SetDefinitionMapping(filter, FmImportMappingKind.ReuseExisting, reversed.Id);
        FmImportEffectiveConnection result = plan.Effective.ConnectionByKey[cutoff];
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.DestinationPortIdentity, Is.EqualTo(cutoff.PortIdentity));
        Assert.That(result.DestinationPortIndex, Is.GreaterThan(sourceIndex),
            "semantic port mapping must follow the stage even if it moved");

        // Same visible stage ID/name/kind is *not* sufficient when the
        // generation has changed: previously persisted overrides are stale.
        FilterDefinition regened = Filter((ObjectId)202U, FilterKind.OnePole);
        regened.ChangeStageKind(1, FilterKind.SvfLowPass);
        regened.ChangeStageKind(1, FilterKind.OnePole);
        FmSynthImportPlan second = new([graph], destinationFilters: [regened]);
        second.SetDefinitionMapping(filter, FmImportMappingKind.ReuseExisting, regened.Id);
        Assert.That(second.Effective.ConnectionByKey[cutoff].IsActive, Is.False);
        Assert.That(second.Effective.ConnectionByKey[cutoff].InactiveReason,
            Does.Contain("no matching"));
    }

    [Test]
    public void UnusedInputIsExcludedUnlessItWasAuthoredDisconnectedOrPreserved()
    {
        SongDocument song = new();
        FilterDefinition original = Filter(song.AllocateObjectId(), FilterKind.OnePole);
        song.Add(original);
        FmSynthDefinition synth = Add(song, "A", new FmSynthGraph([
            new FmConstantNode(1, 0.5),
            new FmConstantNode(9, 0.25), // Authored disconnected fragment.
            FilterNode(4, original, audio: null, modulator: 1),
        ], 4));
        FilterDefinition changed = Filter((ObjectId)300U, FilterKind.SvfNotch);
        FmImportDiscoveredGraph graph = Discover(song, synth);
        FmSynthImportPlan plan = new([graph], destinationFilters: [changed]);
        FmImportNodeKey liveChild = new(graph.SelectedSynthRoots[0], 1);
        FmImportNodeKey disconnected = new(graph.SelectedSynthRoots[0], 9);
        FmImportGraphInputKey cutoff = FmImportGraphInputKey.From(graph.Connections.Single(
            x => x.Target.NodeId == 4));
        FmImportObjectKey filter = new(graph.SourceDocumentIdentity, original.Id);

        Assert.That(plan.Effective.NodeByKey[liveChild].IsIncluded, Is.True);
        Assert.That(plan.Effective.NodeByKey[disconnected].IsIncluded, Is.True);
        plan.SetDefinitionMapping(filter, FmImportMappingKind.ReuseExisting, changed.Id);
        Assert.That(plan.Effective.ConnectionByKey[cutoff].IsActive, Is.False);
        Assert.That(plan.Effective.NodeByKey[liveChild].IsIncluded, Is.False,
            "formerly connected node must not be silently imported as orphan");
        Assert.That(plan.Effective.NodeByKey[disconnected].IsIncluded, Is.True);
        plan.SetPreserveDisconnectedNode(liveChild, true);
        Assert.That(plan.Effective.NodeByKey[liveChild].IsIncluded, Is.True);
        plan.SetPreserveDisconnectedNode(liveChild, false);
        Assert.That(plan.Effective.NodeByKey[liveChild].IsIncluded, Is.False);
    }

    [Test]
    public void SharedDependencyIsCopiedOnlyWhileSomeLiveUseStillRequiresIt()
    {
        SongDocument song = new();
        FmSynthDefinition child = Add(song, "Child", new FmSynthGraph(
            [new FmConstantNode(0, 0.5)], 0));
        FmSynthDefinition a = Add(song, "A", new FmSynthGraph(
            [new FmSoundSourceNode(0, child.Id, 440)], 0));
        FmSynthDefinition b = Add(song, "B", new FmSynthGraph(
            [new FmSoundSourceNode(0, child.Id, 440)], 0));
        FmImportDiscoveredGraph graph = FmSynthImportGraphDiscovery.Discover(
            new SongFmSynthImportSource("many.hm", song, [a, b, child]),
            [a.Id, b.Id]);
        FmSynthImportPlan plan = new([graph]);
        FmImportObjectKey childKey = new(graph.SourceDocumentIdentity, child.Id);
        FmImportReferenceKey[] uses = graph.ObjectReferences
            .Where(reference => reference.Target == childKey)
            .Select(reference => reference.Use).ToArray();
        Assert.That(uses, Has.Length.EqualTo(2));
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(3));
        plan.SetReferenceOverride(uses[0], FmImportMappingKind.None);
        Assert.That(plan.Effective.ReferenceByUse[uses[0]].IsActive, Is.False);
        Assert.That(plan.Effective.ReferenceByUse[uses[0]].InactiveReason,
            Does.Contain("disconnected"));
        Assert.That(plan.Effective.ObjectByKey[childKey].IsCopied, Is.True);
        plan.SetReferenceOverride(uses[1], FmImportMappingKind.None);
        Assert.That(plan.Effective.ReferenceByUse[uses[1]].IsActive, Is.False);
        Assert.That(plan.Effective.ObjectByKey[childKey].IsCopied, Is.False);
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(2));
        plan.ClearReferenceOverride(uses[1]);
        Assert.That(plan.Effective.ObjectByKey[childKey].IsCopied, Is.True);
    }

    [Test]
    public void StaleFilterMetadataNeverCreatesAnActiveEdgeOrCopy()
    {
        SongDocument song = new();
        FilterDefinition filter = Filter(song.AllocateObjectId(), FilterKind.OnePole);
        song.Add(filter);
        FmFilterNode node = new(2, null, new FilterInstance(filter),
            [new FmFilterParameterConnection(999, FilterKind.OnePole, 0,
                "CutoffHz", 1)]);
        FmSynthDefinition synth = Add(song, "Stale", new FmSynthGraph([
            new FmConstantNode(1, 0.5), node,
        ], 2));
        FmImportDiscoveredGraph graph = Discover(song, synth);
        FmSynthImportPlan plan = new([graph]);
        FmImportEffectiveConnection stale = plan.Effective.Connections.Single();
        Assert.That(stale.IsActive, Is.False);
        Assert.That(stale.InactiveReason, Does.Contain("stale"));
        Assert.That(plan.Effective.NodeByKey[
            new FmImportNodeKey(graph.SelectedSynthRoots[0], 1)].IsIncluded, Is.True,
            "an originally disconnected authored node must remain included");
        Assert.Throws<InvalidOperationException>(() =>
            plan.SetGraphInputConnected(stale.Key, true));
    }

    private static FmFilterNode FilterNode(int id, FilterDefinition definition,
        int? audio, int modulator)
        => new(id, audio, new FilterInstance(definition),
            [new FmFilterParameterConnection(1,
                definition.GetStage(1).Kind, 0, "CutoffHz", modulator)]);

    private static FilterDefinition Filter(ObjectId id, FilterKind kind)
        => new(id, "Filter", [new FilterStageDefinition(1, "stage", kind)]);

    private static FmImportDiscoveredGraph Discover(
        SongDocument document, FmSynthDefinition synth)
        => FmSynthImportGraphDiscovery.Discover(
            new SongFmSynthImportSource(Path.GetFullPath("filter-map.hm"),
                document, [synth]), [synth.Id]);

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }
}
