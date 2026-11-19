using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportExactMatcherTests
{
    [Test]
    public void NodeEnvelopeExactReuseIgnoresNameAndIdButPreservesManualProvenance()
    {
        SongDocument source = new();
        NodeEnvelopeDefinition envelope = Node(source, "Source envelope", .65);
        FmSynthDefinition root = Synth(source, "Lead", envelope.Id, null);
        SongDocument destination = new();
        NodeEnvelopeDefinition misleading = Node(destination, "Source envelope", .5);
        NodeEnvelopeDefinition exact = Node(destination, "Unrelated name", .65);
        FmImportExactMatchSource input = Source("nodematch.hm", source, root);
        uint fromRevision = source.DocumentRevision;
        uint destinationRevision = destination.DocumentRevision;

        FmSynthImportPlan plan = FmSynthImportExactMatcher.CreatePlan([input], destination);
        FmImportObjectKey key = new(input.Discovery.SourceDocumentIdentity, envelope.Id);
        FmImportMapping initial = plan.GetDefinitionMapping(key);
        Assert.That(misleading.Id.Value, Is.LessThan(exact.Id.Value));
        Assert.That(initial.Kind, Is.EqualTo(FmImportMappingKind.ReuseExisting));
        Assert.That(initial.ExistingId, Is.EqualTo(exact.Id));
        Assert.That(initial.Provenance,
            Is.EqualTo(FmImportMappingProvenance.AutomaticExactMatch));
        Assert.That(plan.Effective.ObjectByKey[key].IsCopied, Is.False);
        Assert.That(plan.Effective.ReusedReferenceCount, Is.EqualTo(1));
        Assert.That(plan.GetDefinitionMapping(input.Discovery.SelectedSynthRoots[0]).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy),
            "selected Synth roots must never be automatically deduplicated");

        plan.SetDefinitionMapping(key, FmImportMappingKind.ImportCopy);
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Manual));
        plan.SetDefinitionMapping(key, FmImportMappingKind.ReuseExisting, exact.Id);
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.Manual));
        plan.ResetToDefaults();
        Assert.That(plan.GetDefinitionMapping(key).ExistingId, Is.EqualTo(exact.Id));
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.AutomaticExactMatch));
        Assert.That(source.DocumentRevision, Is.EqualTo(fromRevision));
        Assert.That(destination.DocumentRevision, Is.EqualTo(destinationRevision));
    }

    [Test]
    public void NodeEnvelopeStrictEqualityChecksCurveSustainModeAndDisplayRange()
    {
        SongDocument song = new();
        NodeEnvelopeDefinition original = Node(song, "Original", .7);
        NodeEnvelopeDefinition other = Node(song, "Other", .7);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.True);

        other.SetDisplayRange(-1, 2);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.False,
            "persisted editor display range belongs to strict object identity");
        other.SetDisplayRange(original.Vmin, original.Vmax);
        other.ReplaceCurve(other.Nodes, 1, 2, EnvelopeSustainLoopMode.PingPong);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.False);
        other.ReplaceCurve(other.Nodes, 1, 2, EnvelopeSustainLoopMode.Forward);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.True);

        other.ReplaceCurve([
            new EnvelopeNode(TimeSpan.Zero, .0),
            new EnvelopeNode(TimeSpan.FromMilliseconds(10), .7),
            new EnvelopeNode(TimeSpan.FromMilliseconds(150), 1),
        ], 1, 2, EnvelopeSustainLoopMode.Forward);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.False);
        other.ReplaceCurve([
            new EnvelopeNode(TimeSpan.Zero, .0),
            new EnvelopeNode(TimeSpan.FromMilliseconds(10), 1),
            new EnvelopeNode(TimeSpan.FromMilliseconds(151), .7),
        ], 1, 2, EnvelopeSustainLoopMode.Forward);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.False,
            "a one-millisecond timestamp difference must be detected");
        other.ReplaceCurve(original.Nodes, 0, 2, EnvelopeSustainLoopMode.Forward);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, other), Is.False);
    }

    [Test]
    public void ScriptEnvelopeExpressionAndReleaseDurationBothMatter()
    {
        SongDocument song = new();
        ScriptEnvelopeDefinition original = new(song.AllocateObjectId(), "Script");
        original.Source = "releaseSeconds < 0 ? 1.0 : 0.0";
        original.ReleaseDuration = TimeSpan.FromMilliseconds(500);
        song.Add(original);
        ScriptEnvelopeDefinition differentWhitespace =
            new(song.AllocateObjectId(), "Script");
        differentWhitespace.Source = "releaseSeconds<0 ? 1.0 : 0.0";
        differentWhitespace.ReleaseDuration = original.ReleaseDuration;
        song.Add(differentWhitespace);
        ScriptEnvelopeDefinition differentRelease =
            new(song.AllocateObjectId(), "Script");
        differentRelease.Source = original.Source;
        differentRelease.ReleaseDuration = TimeSpan.FromMilliseconds(400);
        song.Add(differentRelease);
        ScriptEnvelopeDefinition identical = new(song.AllocateObjectId(), "Different");
        identical.Source = original.Source;
        identical.ReleaseDuration = original.ReleaseDuration;
        song.Add(identical);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original,
            differentWhitespace), Is.False);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original,
            differentRelease), Is.False);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, identical),
            Is.True);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original,
            Node(song, "Node", 1)), Is.False);
    }

    [Test]
    public void FilterExactMatchRequiresOrderedStagesDefaultsNamesAndHistory()
    {
        FilterDefinition source = Filter((ObjectId)12U, "Source");
        FilterDefinition exact = CopyFilter(source, (ObjectId)90U, "Different name");
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, exact), Is.True);

        FilterDefinition changedDefault = CopyFilter(source, (ObjectId)91U, "Source");
        changedDefault.SetStageDefaults(1, [
            new KeyValuePair<string, FilterParameterValue>("CutoffHz",
                FilterParameterValue.FromNumber(2000))]);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, changedDefault),
            Is.False);

        FilterDefinition reordered = CopyFilter(source, (ObjectId)92U, "Source");
        reordered.MoveStage(1, 1);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, reordered),
            Is.False);

        FilterDefinition renamedStage = CopyFilter(source, (ObjectId)93U, "Source");
        renamedStage.RenameStage(1, "renamed");
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, renamedStage),
            Is.False, "stage name is used by textual parameter paths");

        FilterDefinition changedKind = CopyFilter(source, (ObjectId)94U, "Source");
        changedKind.ChangeStageKind(1, FilterKind.SvfLowPass);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, changedKind),
            Is.False);

        FilterDefinition differentNextId =
            CopyFilter(source, (ObjectId)95U, "Source");
        differentNextId.AddStage("extra", FilterKind.OnePole);
        differentNextId.RemoveStage(4);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, differentNextId),
            Is.False, "allocator history is persisted identity");

        FilterDefinition differentGeneration =
            new((ObjectId)96U, "Same defaults", source.Stages);
        // Even with matching active stage kinds and defaults, restored
        // generation history of an old stage can differ.
        Dictionary<int, long> generations =
            source.StageGenerationsForPersistence.ToDictionary(x => x.Key, x => x.Value);
        generations[1] = generations[1] + 1;
        differentGeneration.RestoreStageIdentity(source.NextStageIdForPersistence,
            generations);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, differentGeneration),
            Is.False);

        FilterDefinition differentTombstone =
            new((ObjectId)97U, "Same defaults", source.Stages);
        Dictionary<int, long> former =
            source.StageGenerationsForPersistence.ToDictionary(x => x.Key, x => x.Value);
        former[3] = former[3] + 1;
        differentTombstone.RestoreStageIdentity(source.NextStageIdForPersistence, former);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(source, differentTombstone),
            Is.False, "removed-stage generation is part of exact persisted identity");
    }

    [Test]
    public void FilterReusePicksSmallestExactIdAndUpdatesEffectiveSchema()
    {
        SongDocument source = new();
        FilterDefinition filter = Filter(source.AllocateObjectId(), "Imported");
        source.Add(filter);
        FmSynthDefinition synth = Synth(source, "Filter synth", null, filter);
        SongDocument destination = new();
        FilterDefinition bad = CopyFilter(filter,
            destination.AllocateObjectId(), "Imported");
        bad.RenameStage(1, "wrongname");
        destination.Add(bad);
        FilterDefinition winner = CopyFilter(filter,
            destination.AllocateObjectId(), "Different");
        destination.Add(winner);
        FilterDefinition later = CopyFilter(filter,
            destination.AllocateObjectId(), "Another");
        destination.Add(later);

        FmImportExactMatchSource input = Source("filter.hm", source, synth);
        FmSynthImportPlan plan = FmSynthImportExactMatcher.CreatePlan([input],
            destination);
        FmImportObjectKey key = new(input.Discovery.SourceDocumentIdentity, filter.Id);
        Assert.That(plan.GetDefinitionMapping(key).ExistingId, Is.EqualTo(winner.Id));
        Assert.That(plan.GetDefinitionMapping(key).Provenance,
            Is.EqualTo(FmImportMappingProvenance.AutomaticExactMatch));
        Assert.That(plan.Effective.ObjectByKey[key].IsCopied, Is.False);
        Assert.That(plan.Effective.ReusedReferenceCount, Is.EqualTo(1));

        // Later live destination edits don't retroactively rewrite a user's
        // current import plan. The click-time validator must recheck them.
        winner.RenameStage(1, "now_changed");
        Assert.That(plan.GetDefinitionMapping(key).ExistingId, Is.EqualTo(winner.Id));
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(filter, winner),
            Is.False);
        plan.ResetToDefaults();
        Assert.That(plan.GetDefinitionMapping(key).ExistingId, Is.EqualTo(winner.Id),
            "Reset recreates the initial captured suggestion, not a new scan");
    }

    [Test]
    public void SameNumericIdAndNameNeverOverrideUnsavedSourceDifferences()
    {
        SongDocument source = new();
        NodeEnvelopeDefinition envelope = Node(source, "Same name", .8);
        FmSynthDefinition root = Synth(source, "Root", envelope.Id, null);
        SongDocument destination = new();
        NodeEnvelopeDefinition local = Node(destination, "Same name", .8);
        Assert.That(envelope.Id, Is.EqualTo(local.Id));
        local.SetDisplayRange(-1, 1);
        FmImportExactMatchSource input = Source("sameid.hm", source, root);

        Assert.That(FmSynthImportExactMatcher.Find([input], destination), Is.Empty);
        FmSynthImportPlan plan = FmSynthImportExactMatcher.CreatePlan([input],
            destination);
        FmImportObjectKey key = new(input.Discovery.SourceDocumentIdentity, envelope.Id);
        Assert.That(plan.GetDefinitionMapping(key).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy));
    }

    [Test]
    public void MultiSourceCollidingObjectIdsRemainSeparateRecommendations()
    {
        SongDocument left = new();
        NodeEnvelopeDefinition leftEnvelope = Node(left, "Left", .7);
        FmSynthDefinition leftRoot = Synth(left, "Left root", leftEnvelope.Id, null);
        SongDocument right = new();
        NodeEnvelopeDefinition rightEnvelope = Node(right, "Right", .2);
        FmSynthDefinition rightRoot = Synth(right, "Right root", rightEnvelope.Id, null);
        Assert.That(leftEnvelope.Id, Is.EqualTo(rightEnvelope.Id));
        SongDocument destination = new();
        NodeEnvelopeDefinition leftTarget = Node(destination, "X", .7);
        NodeEnvelopeDefinition rightTarget = Node(destination, "Y", .2);
        FmImportExactMatchSource fromLeft = Source("left.hm", left, leftRoot);
        FmImportExactMatchSource fromRight = Source("right.hm", right, rightRoot);

        FmSynthImportPlan plan =
            FmSynthImportExactMatcher.CreatePlan([fromLeft, fromRight], destination);
        FmImportObjectKey leftKey =
            new(fromLeft.Discovery.SourceDocumentIdentity, leftEnvelope.Id);
        FmImportObjectKey rightKey =
            new(fromRight.Discovery.SourceDocumentIdentity, rightEnvelope.Id);
        Assert.That(leftKey, Is.Not.EqualTo(rightKey));
        Assert.That(plan.GetDefinitionMapping(leftKey).ExistingId,
            Is.EqualTo(leftTarget.Id));
        Assert.That(plan.GetDefinitionMapping(rightKey).ExistingId,
            Is.EqualTo(rightTarget.Id));
        Assert.That(plan.SelectedRoots, Has.Count.EqualTo(2));
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(2),
            "only the two selected Synth roots need copying");
        Assert.Throws<ArgumentException>(() =>
            FmSynthImportExactMatcher.Find([fromLeft, fromLeft], destination));
    }

    [Test]
    public void SampleAutomaticReuseHashesDecodedPcmThenChecksActualDataAndMetadata()
    {
        SongDocument source = new();
        SampleDefinition original = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Source clip", "tone.wav", Wave(1234));
        source.Add(original);
        FmSynthDefinition root = AddSampleRoot(source, original.Id);
        SongDocument destination = new();
        SampleDefinition wrong = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Source clip", "other.wav", Wave(4321));
        destination.Add(wrong);
        SampleDefinition exact = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Other title", "renamed.wav", Wave(1234));
        destination.Add(exact);
        Assert.That(original.SourceColourArgb, Is.Not.Null);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, wrong),
            Is.False);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, exact),
            Is.True, "sample equality is based on PCM content, not name or UI tint");

        FmImportExactMatchSource input = Source("sample-dedup.hm", source, root);
        FmSynthImportPlan plan =
            FmSynthImportExactMatcher.CreatePlan([input], destination);
        FmImportObjectKey key =
            new(input.Discovery.SourceDocumentIdentity, original.Id);
        Assert.That(plan.GetDefinitionMapping(key), Is.EqualTo(
            new FmImportMapping(FmImportMappingKind.ReuseExisting, exact.Id,
                FmImportMappingProvenance.AutomaticExactMatch)));
        Assert.That(plan.Effective.ObjectByKey[key].IsCopied, Is.False);
        FmImportStageBResult checkedPlan =
            FmSynthImportStageBPreflight.Check(plan, [input], destination);
        Assert.That(checkedPlan.IsPreflightValid, Is.True,
            string.Join(" | ", checkedPlan.Issues.Select(x => x.Message)));
        Assert.That(checkedPlan.PreparedSamples, Is.Empty,
            "reused Sample must not read or stage another encoded copy");
        FmImportCommitResult commit =
            FmSynthImportTransactionCommitter.Commit(plan, [input], destination);
        Assert.That(commit.IsCommitted, Is.True, commit.Error);
        Assert.That(commit.Edit!.ImportedObjectIds, Has.Count.EqualTo(1),
            "the selected root copies, but the matching Sample is reused");
        FmSynthDefinition added = (FmSynthDefinition)destination.Objects[
            commit.SelectedRootIds.Single()];
        Assert.That(added.Graph.Nodes.OfType<FmSoundSourceNode>().Single().SourceId,
            Is.EqualTo(exact.Id));
        Assert.That(destination.Objects.Values.OfType<SampleDefinition>().ToArray(),
            Has.Count.EqualTo(2));
    }

    [Test]
    public void IdenticalPcmWithDifferentLoopOrSpatialPositionsMustNotReuse()
    {
        SongDocument source = new();
        SampleDefinition original = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Clip", "tone.wav", Wave(1234));
        source.Add(original);
        FmSynthDefinition root = AddSampleRoot(source, original.Id);
        SongDocument destination = new();

        SampleDefinition changedLoop = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Looped", "tone.wav", Wave(1234));
        changedLoop.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 1);
        destination.Add(changedLoop);

        SampleDefinition movedChannel = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Moved", "tone.wav", Wave(1234));
        movedChannel.SourceChannelPositions.Add(new Vector3(1, 0, 0));
        destination.Add(movedChannel);

        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, changedLoop),
            Is.False);
        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, movedChannel),
            Is.False);
        FmImportExactMatchSource input = Source("sample-metadata.hm", source, root);
        FmSynthImportPlan plan =
            FmSynthImportExactMatcher.CreatePlan([input], destination);
        FmImportObjectKey key =
            new(input.Discovery.SourceDocumentIdentity, original.Id);
        Assert.That(plan.GetDefinitionMapping(key).Kind,
            Is.EqualTo(FmImportMappingKind.ImportCopy));
        Assert.That(plan.Effective.ObjectByKey[key].IsCopied, Is.True);
    }

    [Test]
    public void HashAndDeepSampleEqualityRejectDifferentAudioDespiteSameName()
    {
        SongDocument source = new();
        SampleDefinition original = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Clip", "clip.wav", Wave(100));
        source.Add(original);
        FmSynthDefinition root = AddSampleRoot(source, original.Id);
        SongDocument destination = new();
        SampleDefinition local = SampleDefinition.CreateImported(
            destination.AllocateObjectId(), "Clip", "clip.wav", Wave(101));
        destination.Add(local);

        Assert.That(FmSynthImportExactMatcher.StrictlyEqual(original, local),
            Is.False);
        FmImportExactMatchSource input = Source("different-audio.hm", source, root);
        Assert.That(FmSynthImportExactMatcher.Find([input], destination),
            Is.Empty);
    }

    private static FmSynthDefinition AddSampleRoot(SongDocument song,
        ObjectId sampleId)
    {
        FmSynthDefinition root = new(song.AllocateObjectId(), "Sample root",
            new FmSynthGraph([new FmSoundSourceNode(0, sampleId, 440)], 0));
        song.Add(root);
        return root;
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

    private static NodeEnvelopeDefinition Node(
        SongDocument song, string name, double releaseValue)
    {
        NodeEnvelopeDefinition envelope = new(song.AllocateObjectId(), name);
        envelope.ReplaceCurve([
            new EnvelopeNode(TimeSpan.Zero, .0),
            new EnvelopeNode(TimeSpan.FromMilliseconds(10), 1),
            new EnvelopeNode(TimeSpan.FromMilliseconds(150), releaseValue),
        ], 1, 2, EnvelopeSustainLoopMode.Forward);
        song.Add(envelope);
        return envelope;
    }

    private static FilterDefinition Filter(ObjectId id, string name)
    {
        FilterDefinition filter = new(id, name, [
            new FilterStageDefinition(1, "stage1", FilterKind.OnePole),
            new FilterStageDefinition(2, "stage2", FilterKind.SvfLowPass),
        ]);
        filter.AddStage("history", FilterKind.OnePole);
        filter.RemoveStage(3);
        return filter;
    }

    private static FilterDefinition CopyFilter(
        FilterDefinition source, ObjectId id, string name)
    {
        FilterDefinition clone = new(id, name, source.Stages);
        clone.RestoreStageIdentity(source.NextStageIdForPersistence,
            source.StageGenerationsForPersistence);
        return clone;
    }

    private static FmSynthDefinition Synth(
        SongDocument song, string name, ObjectId? envelopeId,
        FilterDefinition? filter)
    {
        FmSynthNode node = envelopeId is ObjectId id
            ? new FmEnvelopeNode(0, id)
            : new FmFilterNode(0, configuration: new FilterInstance(filter!));
        FmSynthDefinition synth = new(song.AllocateObjectId(), name,
            new FmSynthGraph([node], 0));
        song.Add(synth);
        return synth;
    }

    private static FmImportExactMatchSource Source(
        string path, SongDocument song, FmSynthDefinition root)
    {
        SongFmSynthImportSource source = new(Path.GetFullPath(path), song, [root]);
        return new(source, FmSynthImportGraphDiscovery.Discover(source, [root.Id]));
    }
}
