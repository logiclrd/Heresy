using System;
using System.IO;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportTreeProjectionTests
{
    [Test]
    public void SelectedSynthHasNestedSemanticInputsAndImportSummary()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Music",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 440),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmMultiNode(4, 3),
            ], 4));
        SongDocument destination = new();
        FmImportExactMatchSource input = Discover("graph.hm", source, root);
        FmSynthImportPlan plan = Plan(destination, input);
        FmSynthImportTreeProjection projection = new(plan);

        Assert.That(projection.Roots, Has.Count.EqualTo(1));
        FmImportTreeRow top = projection.Roots.Single();
        Assert.That(top.Label, Does.Contain("Music"));
        Assert.That(top.Action, Does.Contain("Import copy"));
        Assert.That(top.Children, Has.Count.EqualTo(1));
        FmImportTreeRow multi = top.Children.Single();
        Assert.That(multi.Label, Does.Contain("Multi"));
        Assert.That(multi.Children.Single().Label, Does.Contain("Audio"));
        FmImportTreeRow operation = multi.Children.Single();
        Assert.That(operation.Children.Count(x => x.Label.Contains("Operand")), Is.EqualTo(2));
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(1));
        Assert.That(projection.Summary.NewObjects.Single(), Does.Contain("Music"));
        Assert.That(projection.Summary.CanImport, Is.True);
        Assert.That(projection.Summary.ErrorCount, Is.Zero);
    }

    [Test]
    public void RepeatedOperandHasOneCanonicalAndAliasesNavigateToIt()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Shared",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmOperatorNode(2, FmOperatorKind.Add, [1, 1]),
            ], 2));
        FmSynthImportPlan plan = Plan(new SongDocument(),
            Discover("shared.hm", source, root));
        FmSynthImportTreeProjection projection = new(plan);
        FmImportNodeKey node = new(plan.SelectedRoots.Single(), 1);
        FmImportEntityKey entity = FmImportEntityKey.ForNode(node);
        FmImportTreeRow[] operandRows = All(projection).Where(x =>
            x.Kind == FmImportTreeRowKind.GraphInput).ToArray();
        Assert.That(operandRows, Has.Length.EqualTo(2));
        Assert.That(operandRows.Count(x => x.IsCanonical), Is.EqualTo(1));
        Assert.That(operandRows.Count(x => x.IsAlias), Is.EqualTo(1));
        FmImportTreeRow initialOwner = operandRows.Single(x => x.IsCanonical);
        FmImportTreeRow alias = operandRows.Single(x => x.IsAlias);

        projection.SetExpanded(projection.Roots.Single().Key, true);
        projection.Select(alias.Key);
        projection.Focus(alias.Key);
        FmImportTreeRowKey? linked = projection.NavigateToCanonical(alias.Key);
        Assert.That(linked, Is.EqualTo(initialOwner.Key));
        Assert.That(projection.Selected, Is.EqualTo(initialOwner.Key));
        Assert.That(projection.Focused, Is.EqualTo(initialOwner.Key));
        Assert.That(projection.Rows[projection.Roots.Single().Key].IsExpanded, Is.True);

        projection.SetConnection(initialOwner.Input!.Value, false);
        Assert.That(plan.GetCanonical(entity), Is.EqualTo(
            alias.Key.Occurrence!.Value), "canonical ownership must move to another live use");
        Assert.That(projection.Rows[alias.Key].IsCanonical, Is.True);
        Assert.That(projection.Rows[initialOwner.Key].IsAlias, Is.True);
        Assert.That(projection.Summary.DisconnectedInputCount, Is.EqualTo(1));

        projection.SetConnection(initialOwner.Input.Value, true);
        Assert.That(plan.GetCanonical(entity), Is.EqualTo(alias.Key.Occurrence.Value),
            "restoring a previously canonical connection must not steal ownership back");
        Assert.That(projection.Rows[alias.Key].IsCanonical, Is.True);
        Assert.That(projection.Summary.DisconnectedInputCount, Is.Zero);
    }

    [Test]
    public void AutomaticEnvelopeReuseIsOrangeOnlyUntilManualIntervention()
    {
        SongDocument source = new();
        NodeEnvelopeDefinition envelope = new(source.AllocateObjectId(), "Imported");
        source.Add(envelope);
        FmSynthDefinition root = Add(source, "Envelope synth",
            new FmSynthGraph([new FmEnvelopeNode(0, envelope.Id)], 0));
        SongDocument destination = new();
        NodeEnvelopeDefinition local = new(destination.AllocateObjectId(), "Local");
        destination.Add(local);
        FmImportExactMatchSource input = Discover("envelope.hm", source, root);
        FmSynthImportPlan plan = Plan(destination, input);
        FmSynthImportTreeProjection projection = new(plan);
        FmImportObjectKey key =
            new(input.Discovery.SourceDocumentIdentity, envelope.Id);
        FmImportTreeRow matched = All(projection).Single(x => x.Object == key);

        Assert.That(matched.IsAutomaticExactMatch, Is.True);
        Assert.That(matched.Action, Does.Contain("Automatic exact match"));
        Assert.That(projection.Summary.AutomaticallyReusedObjectCount, Is.EqualTo(1));
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(1));
        projection.ApplyDefinition(key, FmImportMappingKind.ImportCopy);
        Assert.That(All(projection).Single(x => x.Object == key)
            .IsAutomaticExactMatch, Is.False);
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(2));
        projection.ApplyDefinition(key, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(All(projection).Single(x => x.Object == key)
            .IsAutomaticExactMatch, Is.False,
            "manual reselection must not reacquire orange automatic provenance");
        projection.Reset();
        Assert.That(All(projection).Single(x => x.Object == key)
            .IsAutomaticExactMatch, Is.True);
    }

    [Test]
    public void PerUseOverridesChangeSummaryButDoNotGloballySplitSharedSource()
    {
        SongDocument source = new();
        FmSynthDefinition child = Add(source, "Child",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthDefinition root = Add(source, "Two voices",
            new FmSynthGraph([
                new FmSoundSourceNode(1, child.Id, 440),
                new FmSoundSourceNode(2, child.Id, 440),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            ], 3));
        SongDocument destination = new();
        FmSynthDefinition local = Add(destination, "Substitute",
            new FmSynthGraph([new FmNoiseNode(0)], 0));
        FmImportExactMatchSource input = Discover("voices.hm", source, root);
        FmSynthImportPlan plan = Plan(destination, input);
        FmSynthImportTreeProjection projection = new(plan);
        FmImportReferenceKey[] uses = projection.Rows.Values
            .Where(x => x.Reference is not null &&
                x.Reference.Value.Slot == "Source")
            .Select(x => x.Reference!.Value).Distinct().ToArray();
        Assert.That(uses, Has.Length.EqualTo(2));
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(2));
        uint sourceRev = source.DocumentRevision, destRev = destination.DocumentRevision;

        projection.ApplyUse(uses[0], FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(2));
        Assert.That(projection.Summary.ReusedUseCount, Is.EqualTo(1));
        Assert.That(projection.Summary.ExplicitlyRemappedUseCount, Is.EqualTo(1));
        projection.ApplyUse(uses[1], FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(1));
        Assert.That(projection.Summary.ReusedUseCount, Is.EqualTo(2));
        projection.ClearUse(uses[1]);
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(2));
        Assert.That(plan.GetReferenceMapping(uses[1]).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy));
        Assert.That(source.DocumentRevision, Is.EqualTo(sourceRev));
        Assert.That(destination.DocumentRevision, Is.EqualTo(destRev));
    }

    [Test]
    public void FilterPortDisconnectionDimsChildAndPreservesOtherLiveUse()
    {
        SongDocument source = new();
        FilterDefinition original = new(source.AllocateObjectId(), "Original");
        source.Add(original);
        FmSynthDefinition root = Add(source, "Filter synth",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmOperatorNode(2, FmOperatorKind.Add, [1]),
                new FmFilterNode(3, 2, new FilterInstance(original),
                    [new FmFilterParameterConnection(1, FilterKind.OnePole,
                        0, "CutoffHz", 1)]),
            ], 3));
        SongDocument destination = new();
        FilterDefinition other = new(destination.AllocateObjectId(), "Other",
            [new FilterStageDefinition(1, "stage", FilterKind.SvfNotch)]);
        destination.Add(other);
        FmImportExactMatchSource input = Discover("filter.hm", source, root);
        FmSynthImportPlan plan = Plan(destination, input);
        FmSynthImportTreeProjection projection = new(plan);
        FmImportReferenceKey filter = plan.Effective.References
            .Single(x => x.Use.Slot == "Filter").Use;
        FmImportTreeRow cutoff = All(projection)
            .Single(x => x.Input?.PortIdentity.Contains("CutoffHz") == true);
        Assert.That(cutoff.IsActive, Is.True);
        projection.ApplyUse(filter, FmImportMappingKind.ReuseExisting, other.Id);
        FmImportTreeRow inactive = All(projection)
            .Single(x => x.Input?.PortIdentity.Contains("CutoffHz") == true);
        Assert.That(inactive.IsActive, Is.False);
        Assert.That(inactive.IsDimmed, Is.True);
        Assert.That(inactive.Reason, Does.Contain("no matching"));
        Assert.That(plan.Effective.NodeByKey[new FmImportNodeKey(
            plan.SelectedRoots.Single(), 1)].IsIncluded, Is.True);
        Assert.That(projection.Summary.DisconnectedInputCount, Is.GreaterThan(0));
        projection.ClearUse(filter);
        Assert.That(All(projection).Single(x =>
            x.Input?.PortIdentity.Contains("CutoffHz") == true).IsActive, Is.True);
    }

    [Test]
    public void MissingAndUnassignedSourceRowsAreEditableAndUnassignedIsNotAnEntity()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Empty",
            new FmSynthGraph([
                new FmSoundSourceNode(0, ObjectId.None, 440),
            ], 0));
        FmSynthImportTreeProjection projection = new(
            Plan(new SongDocument(), Discover("none.hm", source, root)));
        FmImportTreeRow row = All(projection).Single(x =>
            x.Reference?.Slot == "Source");
        Assert.That(row.Kind, Is.EqualTo(FmImportTreeRowKind.Reference));
        Assert.That(row.Key.UnboundReference, Is.EqualTo(row.Reference));
        Assert.That(row.AvailableEdits.HasFlag(FmImportTreeEdit.OverrideUse), Is.True);
        Assert.That(row.Canonical, Is.Null);
        Assert.That(row.Action, Does.Contain("None"));
        Assert.That(projection.Summary.CanImport, Is.True);
    }

    [Test]
    public void AValidationErrorDisablesImportUntilTheGraphIsRepaired()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Needs operands",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmOperatorNode(2, FmOperatorKind.Add, [1]),
            ], 2));
        FmSynthImportTreeProjection projection = new(
            Plan(new SongDocument(), Discover("operator.hm", source, root)));
        FmImportTreeRow operand = All(projection).Single(x =>
            x.Kind == FmImportTreeRowKind.GraphInput);
        Assert.That(projection.Summary.CanImport, Is.True);
        projection.SetConnection(operand.Input!.Value, false);
        Assert.That(projection.Summary.CanImport, Is.False);
        Assert.That(projection.Summary.ErrorCount, Is.GreaterThan(0));
        Assert.That(projection.Summary.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.InvalidGraph), Is.True);
        projection.SetConnection(operand.Input.Value, true);
        Assert.That(projection.Summary.CanImport, Is.True);
        Assert.That(projection.Summary.ErrorCount, Is.Zero);
    }

    [Test]
    public void ProposedNamesRetainExplicitDuplicatesAndSelectionAcrossRefreshes()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Lead",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        SongDocument destination = new();
        Add(destination, "Lead",
            new FmSynthGraph([new FmConstantNode(0, .8)], 0));
        FmSynthImportTreeProjection projection = new(
            Plan(destination, Discover("names.hm", source, root)));
        FmImportTreeRow row = projection.Roots.Single();
        Assert.That(projection.Summary.NewObjects.Single(), Does.Contain("Lead 2"));
        projection.Select(row.Key);
        projection.Focus(row.Key);
        projection.SetExpanded(row.Key, true);
        projection.SetName(projection.Roots.Single().Object!.Value, "Lead");
        Assert.That(projection.Summary.NewObjects.Single(), Does.EndWith("Lead"));
        Assert.That(projection.Selected, Is.EqualTo(row.Key));
        Assert.That(projection.Focused, Is.EqualTo(row.Key));
        Assert.That(projection.Rows[row.Key].IsExpanded, Is.True);
        Assert.That(projection.Summary.CanImport, Is.True);
    }

    [Test]
    public void CrossDocumentIdenticalObjectIdsRemainSeparateRows()
    {
        SongDocument one = new(), two = new();
        FmSynthDefinition left = Add(one, "Left",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition right = Add(two, "Right",
            new FmSynthGraph([new FmConstantNode(0, 2)], 0));
        Assert.That(left.Id, Is.EqualTo(right.Id));
        FmImportTreeProjection projection = new(Plan(new SongDocument(),
            Discover("left.hm", one, left), Discover("right.hm", two, right)));
        Assert.That(projection.Roots, Has.Count.EqualTo(2));
        Assert.That(projection.Roots[0].Key,
            Is.Not.EqualTo(projection.Roots[1].Key));
        Assert.That(projection.Roots.Select(x => x.Object?.SourceDocumentIdentity)
            .Distinct().Count(), Is.EqualTo(2));
        Assert.That(projection.Summary.NewObjectCount, Is.EqualTo(2));
    }

    [Test]
    public void DiscoveryOnlyProjectionDoesNotClaimSuccessfulStageA()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmImportExactMatchSource discovered = Discover("discovery.hm", source, root);
        FmSynthImportTreeProjection projection = new(
            new FmSynthImportPlan([discovered.Discovery]));
        Assert.That(projection.Roots, Has.Count.EqualTo(1));
        Assert.That(projection.Summary.ErrorCount, Is.Zero);
        Assert.That(projection.Summary.CanImport, Is.Null);
    }

    private static FmImportTreeRow[] All(FmSynthImportTreeProjection projection)
        => projection.Rows.Values.ToArray();

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }

    private static FmImportExactMatchSource Discover(string path,
        SongDocument song, FmSynthDefinition synth)
    {
        SongFmSynthImportSource input = new(Path.GetFullPath(path), song, [synth]);
        return new(input, FmSynthImportGraphDiscovery.Discover(input, [synth.Id]));
    }

    private static FmSynthImportPlan Plan(SongDocument destination,
        params FmImportExactMatchSource[] sources)
        => FmSynthImportExactMatcher.CreatePlan(sources, destination);
}
