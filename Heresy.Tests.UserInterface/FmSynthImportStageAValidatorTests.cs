using System;
using System.IO;
using System.Linq;

using Heresy.Core.Assets;
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
public sealed class FmSynthImportStageAValidatorTests
{
    private const double C4 = FmMultiNode.NeutralFrequencyHz;

    [Test]
    public void ManualSoundSourceRemappingCanBeMusicallyDifferentAndStillValid()
    {
        SongDocument source = new();
        SampleDefinition pcm = new(source.AllocateObjectId(), "Saved PCM",
            new ExternalAssetReference(Path.GetFullPath("not-loaded.wav")));
        source.Add(pcm);
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, pcm.Id, C4)], 0));
        SongDocument destination = new();
        FmSynthDefinition different = Add(destination, "Different instrument",
            new FmSynthGraph([new FmNoiseNode(0)], 0));
        uint sourceRevision = source.DocumentRevision;
        uint destinationRevision = destination.DocumentRevision;
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = SingleReference(plan, FmImportReferenceKind.SoundSource);

        Assert.That(plan.StageA, Is.Not.Null);
        Assert.That(plan.StageA!.IsInternallyValid, Is.False,
            "a copied Sample with no decoded PCM is not persistably importable");
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.UnavailableSample), Is.True);

        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, different.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        Assert.That(plan.StageA.ErrorCount, Is.Zero);
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(1));
        Assert.That(plan.Effective.ReusedReferenceCount, Is.EqualTo(1));
        Assert.That(source.DocumentRevision, Is.EqualTo(sourceRevision));
        Assert.That(destination.DocumentRevision, Is.EqualTo(destinationRevision));
        Assert.That(plan.SelectedRoots.Count, Is.EqualTo(1));
    }

    [Test]
    public void WrongKindAndMissingLocalTargetFailAndCanBeRepairedInPlace()
    {
        SongDocument source = new();
        FmSynthDefinition nested = Add(source, "Nested",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, nested.Id, C4)], 0));
        SongDocument destination = new();
        FilterDefinition filter = new(destination.AllocateObjectId(), "Wrong kind");
        destination.Add(filter);
        FmSynthDefinition valid = Add(destination, "Different",
            new FmSynthGraph([new FmOscillatorNode(0,
                FmOscillatorWaveform.Sine, C4)], 0));

        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = SingleReference(plan, FmImportReferenceKind.SoundSource);
        Assert.That(plan.StageA!.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, filter.Id);
        Assert.That(plan.StageA!.IsInternallyValid, Is.False);
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.IncompatibleTarget
            && x.Reference == use), Is.True);
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting,
            (ObjectId)9999U);
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.MissingDestination), Is.True);
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, valid.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(1));
    }

    [Test]
    public void InstrumentToneRequiresSourceButSynthSourceCanBeSilent()
    {
        SongDocument source = new();
        FmSynthDefinition child = Add(source, "Child",
            new FmSynthGraph([new FmConstantNode(0, 0.5)], 0));
        InstrumentDefinition instrument = new(source.AllocateObjectId(), "Keys");
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = child.Id });
        source.Add(instrument);
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, instrument.Id, C4)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Add(destination, "Local",
            new FmSynthGraph([new FmNoiseNode(0)], 0));
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey tone = plan.Effective.References
            .Single(x => x.Use.ToneIndex == 0 &&
                x.Use.Slot == "Source").Use;
        FmImportReferenceKey synthSource = plan.Effective.References
            .Single(x => x.Use.NodeId == 0 &&
                x.Use.Owner.SourceObjectId == root.Id &&
                x.Use.Slot == "Source").Use;

        Assert.That(plan.StageA!.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(tone, FmImportMappingKind.None);
        Assert.That(plan.StageA!.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.RequiredReference
            && x.Reference == tone), Is.True);
        plan.SetReferenceOverride(tone, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(synthSource, FmImportMappingKind.None);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(1));
    }

    [Test]
    public void MissingSourceReferenceNeedsExplicitDisconnectOrLocalRemapping()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Draft",
            new FmSynthGraph([new FmSoundSourceNode(0, (ObjectId)999U, C4)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Add(destination, "Available",
            new FmSynthGraph([new FmConstantNode(0, .2)], 0));
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = SingleReference(plan, FmImportReferenceKind.SoundSource);
        Assert.That(plan.StageA!.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.MissingSource
            && x.Reference == use), Is.True);
        plan.SetReferenceOverride(use, FmImportMappingKind.None);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ImportCopy);
        Assert.That(plan.StageA.IsInternallyValid, Is.False,
            "ImportCopy of an undiscoverable definition must never appear valid");
    }

    [Test]
    public void IncompatibleSavedSourceIsDiagnosedAsWrongTypeNotMissing()
    {
        SongDocument source = new();
        FilterDefinition notSound = new(source.AllocateObjectId(), "Filter");
        source.Add(notSound);
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, notSound.Id, C4)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Add(destination, "Replacement",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));

        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = SingleReference(plan, FmImportReferenceKind.SoundSource);
        Assert.That(plan.StageA!.IsInternallyValid, Is.False);
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.IncompatibleTarget
            && x.Reference == use && x.Message.Contains("FilterDefinition")),
            Is.True, Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
    }

    [Test]
    public void OperatorMustRetainAnOperandWhileOptionalFilterInputsCanDisconnect()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Math",
            new FmSynthGraph([
                new FmConstantNode(1, .2),
                new FmConstantNode(2, .3),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmFilterNode(4, 3),
            ], 4));
        SongDocument destination = new();
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportGraphInputKey[] operands = plan.Effective.Connections
            .Where(x => x.Key.Consumer.NodeId == 3)
            .Select(x => x.Key).ToArray();
        FmImportGraphInputKey audio = plan.Effective.Connections
            .Where(x => x.Key.Consumer.NodeId == 4)
            .Select(x => x.Key).Single();

        Assert.That(plan.StageA!.IsInternallyValid, Is.True, Dump(plan));
        plan.SetGraphInputConnected(operands[0], false);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        Assert.That(plan.StageA.WarningCount, Is.GreaterThan(0));
        plan.SetGraphInputConnected(operands[1], false);
        Assert.That(plan.StageA.IsInternallyValid, Is.False);
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.InvalidGraph &&
            x.Node?.NodeId == 3), Is.True);
        plan.SetGraphInputConnected(operands[0], true);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        plan.SetGraphInputConnected(audio, false);
        Assert.That(plan.StageA.IsInternallyValid, Is.True,
            "the Filter audio input is nullable in Core");
    }

    [Test]
    public void ImportedSourceCycleFailsWithoutScanningTheWholeDestination()
    {
        SongDocument source = new();
        ObjectId rootId = source.AllocateObjectId();
        ObjectId instrumentId = source.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "Loop");
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = rootId });
        source.Add(instrument);
        FmSynthDefinition root = new(rootId, "Root",
            new FmSynthGraph([
                new FmSoundSourceNode(0, instrumentId, C4),
            ], 0));
        source.Add(root);
        SongDocument destination = new();
        FmSynthDefinition local = Add(destination, "Non-cyclic",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = plan.Effective.References.Single(
            x => x.Use.ToneIndex == 0 && x.Use.Slot == "Source").Use;

        Assert.That(plan.StageA!.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.DependencyCycle), Is.True,
            Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True,
            "once the copied cycle is broken, unrelated destination references belong to Stage B");
    }

    [Test]
    public void FilterOverridesMustSurviveLocalMappingOrCauseAnError()
    {
        SongDocument source = new();
        FilterDefinition filter = new(source.AllocateObjectId(), "Original",
            [new FilterStageDefinition(1, "stage", FilterKind.OnePole)]);
        source.Add(filter);
        FilterInstance instance = new(filter);
        instance.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1500));
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmFilterNode(0, configuration: instance)], 0));

        SongDocument destination = new();
        FilterDefinition wrong = new(destination.AllocateObjectId(), "Wrong",
            [new FilterStageDefinition(1, "stage", FilterKind.SvfLowPass)]);
        destination.Add(wrong);
        FilterDefinition compatible = new(destination.AllocateObjectId(), "Compatible",
            [new FilterStageDefinition(1, "stage", FilterKind.OnePole)]);
        compatible.SetStageDefaults(1, [
            new System.Collections.Generic.KeyValuePair<string, FilterParameterValue>(
                "CutoffHz", FilterParameterValue.FromNumber(3200))]);
        destination.Add(compatible);
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportReferenceKey use = SingleReference(plan, FmImportReferenceKind.Filter);

        Assert.That(plan.StageA!.IsInternallyValid, Is.True, Dump(plan));
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, wrong.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.False);
        Assert.That(plan.StageA.Issues.Any(x =>
            x.Kind == FmImportStageAIssueKind.IncompatibleFilterOverrides
            && x.Reference == use), Is.True);
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, compatible.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));

        // Stage-A captures source overrides and target identity once. Later
        // changes to the destination must be checked again at import click.
        wrong.ChangeStageKind(1, FilterKind.OnePole);
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, wrong.Id);
        Assert.That(plan.StageA.IsInternallyValid, Is.False);
    }

    [Test]
    public void InvalidCopiedNameDisablesStageAUntilEditedAndDuplicateNamesAreFine()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        SongDocument destination = new();
        Add(destination, "Root", new FmSynthGraph([new FmNoiseNode(0)], 0));
        FmSynthImportPlan plan = MakePlan(source, root, destination);
        FmImportObjectKey key = plan.SelectedRoots[0];

        Assert.That(plan.StageA!.IsInternallyValid, Is.True, Dump(plan));
        plan.SetProposedName(key, " ");
        Assert.That(plan.StageA.IsInternallyValid, Is.False);
        Assert.That(plan.StageA.Issues.Single(x =>
            x.Kind == FmImportStageAIssueKind.InvalidName).Object,
            Is.EqualTo(key));
        plan.SetProposedName(key, "Root");
        Assert.That(plan.StageA.IsInternallyValid, Is.True, Dump(plan));
        Assert.That(plan.GetProposedName(key).Name, Is.EqualTo("Root"),
            "a deliberately duplicated name is legal");
    }

    [Test]
    public void DiscoveryOnlyPlanDoesNotFalselyClaimToBeValidated()
    {
        SongDocument song = new();
        FmSynthDefinition root = Add(song, "Root",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        SongFmSynthImportSource source = new(Path.GetFullPath("legacy.hm"), song,
            [root]);
        FmImportDiscoveredGraph graph =
            FmSynthImportGraphDiscovery.Discover(source, [root.Id]);
        FmSynthImportPlan discoveryOnly = new([graph]);
        Assert.That(discoveryOnly.StageA, Is.Null);
    }

    private static FmSynthImportPlan MakePlan(
        SongDocument source, FmSynthDefinition root, SongDocument destination)
    {
        SongFmSynthImportSource file = new(Path.GetFullPath("stage-a.hm"),
            source, [root]);
        FmImportDiscoveredGraph discovery =
            FmSynthImportGraphDiscovery.Discover(file, [root.Id]);
        return FmSynthImportExactMatcher.CreatePlan([new(file, discovery)], destination);
    }

    private static FmImportReferenceKey SingleReference(FmSynthImportPlan plan,
        FmImportReferenceKind kind)
        => plan.Effective.References.Single(x => kind switch
        {
            FmImportReferenceKind.SoundSource => x.Use.Slot == "Source",
            FmImportReferenceKind.Filter => x.Use.Slot == "Filter",
            FmImportReferenceKind.Envelope => x.Use.Slot == "Envelope",
            _ => false,
        }).Use;

    private static string Dump(FmSynthImportPlan plan)
        => string.Join(" | ", plan.StageA?.Issues.Select(x => x.Message)
            ?? Array.Empty<string>());

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }
}
