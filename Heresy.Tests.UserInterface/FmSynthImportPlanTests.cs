using System;
using System.IO;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportPlanTests
{
    [Test]
    public void SharedDefinitionUsesHaveStickyCanonicalOwnerAfterRemapAndRestore()
    {
        SongDocument song = new();
        NodeEnvelopeDefinition shared = new(song.AllocateObjectId(), "Shared");
        song.Add(shared);
        FmSynthDefinition a = Add(song, "A", new FmSynthGraph(
            [new FmEnvelopeNode(0, shared.Id)], 0));
        FmSynthDefinition b = Add(song, "B", new FmSynthGraph(
            [new FmEnvelopeNode(5, shared.Id)], 5));
        FmImportDiscoveredGraph discovery = Discover("shared.hm", song, [a, b]);
        FmSynthImportPlan plan = new([discovery]);
        FmImportObjectKey key = new(discovery.SourceDocumentIdentity, shared.Id);
        FmImportEntityKey entity = FmImportEntityKey.ForObject(key);
        FmImportReferenceKey first = discovery.ObjectReferences
            .Single(x => x.Target == key && x.Use.Owner.SourceObjectId == a.Id).Use;
        FmImportReferenceKey second = discovery.ObjectReferences
            .Single(x => x.Target == key && x.Use.Owner.SourceObjectId == b.Id).Use;

        Assert.That(plan.GetDefinitionMapping(key).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy));
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.ObjectUse(key, first)));
        plan.SetReferenceOverride(first, FmImportMappingKind.None);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.ObjectUse(key, second)));
        Assert.That(plan.GetOccurrences(entity).Single(x => x.Key.Reference == first).IsActive,
            Is.False);
        Assert.That(plan.GetReferenceMapping(second).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy),
            "an override of one use must not alter any other use");
        plan.ClearReferenceOverride(first);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.ObjectUse(key, second)),
            "restoring the former owner must not steal the canonical row back");
        Assert.That(plan.GetOccurrences(entity).Count(x => x.IsCanonical), Is.EqualTo(1));
        Assert.That(plan.GetOccurrences(entity).All(x =>
            x.Canonical == plan.GetCanonical(entity)), Is.True);
    }

    [Test]
    public void RepeatedGraphInputsRehomeBySemanticUseAndKeepDisconnectedFragments()
    {
        SongDocument song = new();
        FmSynthDefinition root = Add(song, "Graph", new FmSynthGraph(
            [
                new FmConstantNode(1, 1),
                new FmOperatorNode(2, FmOperatorKind.Add, [1, 1]),
                new FmConstantNode(8, 2),
            ], 2));
        FmImportDiscoveredGraph discovery = Discover("graph.hm", song, [root]);
        FmSynthImportPlan plan = new([discovery]);
        FmImportNodeKey repeated = new(discovery.SelectedSynthRoots.Single(), 1);
        FmImportEntityKey entity = FmImportEntityKey.ForNode(repeated);
        FmImportGraphInputKey[] inputs = discovery.Connections
            .Select(FmImportGraphInputKey.From).OrderBy(x => x.InputIndex).ToArray();

        Assert.That(inputs, Has.Length.EqualTo(2));
        Assert.That(inputs[0].PortIdentity, Is.EqualTo("operand:0"));
        Assert.That(inputs[1].PortIdentity, Is.EqualTo("operand:1"));
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(repeated, inputs[0])));
        plan.SetGraphInputConnected(inputs[0], false);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(repeated, inputs[1])));
        plan.SetGraphInputConnected(inputs[0], true);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeUse(repeated, inputs[1])));
        FmImportNodeKey disconnected = new(discovery.SelectedSynthRoots.Single(), 8);
        Assert.That(plan.GetCanonical(FmImportEntityKey.ForNode(disconnected)),
            Is.EqualTo(FmImportOccurrenceKey.NodeRoot(disconnected)),
            "an already-disconnected authored fragment remains represented");
    }

    [Test]
    public void ExactReuseProvenanceCannotBeRecreatedByManualReselection()
    {
        SongDocument song = new();
        ScriptEnvelopeDefinition envelope = new(song.AllocateObjectId(), "Env");
        song.Add(envelope);
        FmSynthDefinition root = Add(song, "Synth", new FmSynthGraph(
            [new FmEnvelopeNode(0, envelope.Id)], 0));
        FmImportDiscoveredGraph discovery = Discover("exact.hm", song, [root]);
        FmSynthImportPlan plan = new([discovery]);
        FmImportObjectKey key = new(discovery.SourceDocumentIdentity, envelope.Id);
        ObjectId local = (ObjectId)77U;

        Assert.That(plan.RecommendExactReuse(key, local), Is.True);
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.AutomaticExactMatch));
        FmImportReferenceKey use = discovery.ObjectReferences.Single().Use;
        plan.SetReferenceOverride(use, FmImportMappingKind.None);
        plan.ClearReferenceOverride(use);
        Assert.That(plan.GetReferenceMapping(use).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Manual),
            "clearing a manual per-use override must not restore orange");
        plan.SetDefinitionMapping(key, FmImportMappingKind.ImportCopy);
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Manual));
        Assert.That(plan.RecommendExactReuse(key, local), Is.False);
        plan.SetDefinitionMapping(key, FmImportMappingKind.ReuseExisting, local);
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Manual));
        plan.ResetToDefaults();
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Default));
        Assert.That(plan.RecommendExactReuse(key, local), Is.True,
            "only Reset to Defaults may reenable automatic provenance");
    }

    [Test]
    public void EverySelectedRootAlwaysCopiesAndNamesCanDeliberatelyDuplicate()
    {
        SongDocument song = new();
        FmSynthDefinition first = Add(song, "Trumpet", new FmSynthGraph(
            [new FmConstantNode(0, 1)], 0));
        FmSynthDefinition second = Add(song, "Trumpet", new FmSynthGraph(
            [new FmConstantNode(0, 2)], 0));
        FmImportDiscoveredGraph discovery = Discover("names.hm", song, [first, second]);
        DocumentWorkspace destination = new();
        uint sourceRevision = song.DocumentRevision;
        uint destRevision = destination.Document.DocumentRevision;
        FmSynthImportPlan plan = new([discovery],
            [new FmImportExistingName(SongObjectKind.FmSynth, "Trumpet"),
             new FmImportExistingName(SongObjectKind.FmSynth, "Trumpet 2")]);
        FmImportObjectKey left = discovery.SelectedSynthRoots[0];
        FmImportObjectKey right = discovery.SelectedSynthRoots[1];

        Assert.That(plan.GetProposedName(left).Name, Is.EqualTo("Trumpet 3"));
        Assert.That(plan.GetProposedName(right).Name, Is.EqualTo("Trumpet 4"));
        Assert.Throws<InvalidOperationException>(() =>
            plan.SetDefinitionMapping(left, FmImportMappingKind.ReuseExisting, first.Id));
        Assert.That(plan.RecommendExactReuse(left, first.Id), Is.False);
        plan.SetProposedName(left, "Trumpet");
        plan.AcceptProposedName(right);
        Assert.That(plan.GetProposedName(left),
            Is.EqualTo(new FmImportProposedName("Trumpet", true)));
        plan.SetDefinitionMapping(right, FmImportMappingKind.ImportCopy);
        Assert.That(plan.GetProposedName(right).Name, Is.EqualTo("Trumpet 4"));
        Assert.That(plan.SelectedRoots, Is.EqualTo(discovery.SelectedSynthRoots));
        Assert.That(song.DocumentRevision, Is.EqualTo(sourceRevision));
        Assert.That(destination.Document.Objects, Is.Empty);
        Assert.That(destination.Document.DocumentRevision, Is.EqualTo(destRevision),
            "plan edits must not allocate destination IDs or mutate a song");
    }

    [Test]
    public void CrossDocumentIdenticalIdsRemainIndependentAndReuseOneDefinitionPerSource()
    {
        SongDocument firstSong = new();
        FmSynthDefinition first = Add(firstSong, "X", new FmSynthGraph(
            [new FmConstantNode(0, 1)], 0));
        SongDocument secondSong = new();
        FmSynthDefinition second = Add(secondSong, "X", new FmSynthGraph(
            [new FmConstantNode(0, 2)], 0));
        Assert.That(first.Id, Is.EqualTo(second.Id));
        FmImportDiscoveredGraph left = Discover("one.hm", firstSong, [first]);
        FmImportDiscoveredGraph right = Discover("two.hm", secondSong, [second]);
        FmSynthImportPlan plan = new([left, right]);

        Assert.That(plan.SelectedRoots, Has.Count.EqualTo(2));
        Assert.That(plan.SelectedRoots[0], Is.Not.EqualTo(plan.SelectedRoots[1]));
        Assert.That(plan.GetProposedName(plan.SelectedRoots[0]).Name, Is.EqualTo("X"));
        Assert.That(plan.GetProposedName(plan.SelectedRoots[1]).Name, Is.EqualTo("X 2"));
        Assert.Throws<ArgumentException>(() => new FmSynthImportPlan([left, left]));
    }

    [Test]
    public void DisconnectedOrExcludedGraphInputsNeverTakeCanonicalOwnership()
    {
        SongDocument song = new();
        FmSynthDefinition root = Add(song, "A", new FmSynthGraph([
            new FmConstantNode(1, 1),
            new FmOperatorNode(2, FmOperatorKind.Add, [1, 1]),
        ], 2));
        FmImportDiscoveredGraph discovery = Discover("exclude.hm", song, [root]);
        FmSynthImportPlan plan = new([discovery]);
        FmImportNodeKey constant = new(discovery.SelectedSynthRoots.Single(), 1);
        FmImportEntityKey entity = FmImportEntityKey.ForNode(constant);
        FmImportGraphInputKey[] inputs = discovery.Connections
            .Select(FmImportGraphInputKey.From).ToArray();

        plan.SetGraphInputConnected(inputs[0], false);
        plan.SetGraphInputConnected(inputs[1], false);
        plan.SetPreserveDisconnectedNode(constant, true);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeRoot(constant)));
        plan.SetGraphInputConnected(inputs[0], true);
        Assert.That(plan.GetCanonical(entity),
            Is.EqualTo(FmImportOccurrenceKey.NodeRoot(constant)),
            "a valid preserved-root owner must remain sticky");
        plan.SetNodeExcluded(constant, true);
        Assert.That(plan.GetOccurrences(entity).All(x => !x.IsActive), Is.True);
        Assert.Throws<InvalidOperationException>(() =>
            plan.SetNodeExcluded(new FmImportNodeKey(discovery.SelectedSynthRoots.Single(), 2), true));
    }

    private static FmImportDiscoveredGraph Discover(string path,
        SongDocument song, FmSynthDefinition[] roots)
        => FmSynthImportGraphDiscovery.Discover(
            new SongFmSynthImportSource(
                Path.GetFullPath(path), song, roots),
            roots.Select(x => x.Id));

    private static FmSynthDefinition Add(
        SongDocument song, string name, FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }
}
