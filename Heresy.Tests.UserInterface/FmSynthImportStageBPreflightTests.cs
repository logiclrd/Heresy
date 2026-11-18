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
public sealed class FmSynthImportStageBPreflightTests
{
    [Test]
    public void SelectedRootAlwaysCopiesEvenWhenTheDestinationHasTheSameId()
    {
        SongDocument source = new();
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Synth(destination, "Local",
            new FmSynthGraph([new FmConstantNode(0, .75)], 0));
        Assert.That(root.Id, Is.EqualTo(local.Id));
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        uint beforeSource = source.DocumentRevision;
        uint beforeDestination = destination.DocumentRevision;

        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(result.IsPreflightValid, Is.True, Message(result));
        Assert.That(result.PreparedSamples, Is.Empty);
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(1));
        Assert.That(source.DocumentRevision, Is.EqualTo(beforeSource));
        Assert.That(destination.DocumentRevision, Is.EqualTo(beforeDestination));
        Assert.That(destination.Objects.Count, Is.EqualTo(1));
    }

    [Test]
    public void ExistingLocalSoundSourceCycleIsFoundOnlyAtStageB()
    {
        SongDocument source = new();
        FmSynthDefinition child = Synth(source, "Child",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, child.Id, 440)], 0));
        SongDocument destination = new();
        ObjectId synthId = destination.AllocateObjectId();
        ObjectId instrumentId = destination.AllocateObjectId();
        FmSynthDefinition cyclic = new(synthId, "Local A",
            new FmSynthGraph([
                new FmSoundSourceNode(0, instrumentId, 440)], 0));
        InstrumentDefinition other = new(instrumentId, "Local B");
        other.ToneSpecifications.Add(new ToneSpecification { SourceId = synthId });
        destination.Add(cyclic);
        destination.Add(other);
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        FmImportReferenceKey use = plan.Effective.References
            .Single(x => x.Use.Owner.SourceObjectId == root.Id && x.Use.Slot == "Source").Use;

        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, synthId);
        Assert.That(plan.StageA!.IsInternallyValid, Is.True);
        uint srcRev = source.DocumentRevision, dstRev = destination.DocumentRevision;

        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(result.IsPreflightValid, Is.False);
        Assert.That(result.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.DestinationDependencyCycle
            && x.Message.Contains("local")), Is.True, Message(result));
        Assert.That(result.PreparedSamples, Is.Empty);
        Assert.That(source.DocumentRevision, Is.EqualTo(srcRev));
        Assert.That(destination.DocumentRevision, Is.EqualTo(dstRev));
    }

    [Test]
    public void ChangedLocalSourceDependencyIsDetectedWithFreshLiveTraversal()
    {
        SongDocument source = new();
        FmSynthDefinition child = Synth(source, "Imported child",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, child.Id, 440)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Synth(destination, "Local",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        FmImportReferenceKey use = plan.Effective.References
            .Single(x => x.Use.Owner.SourceObjectId == root.Id && x.Use.Slot == "Source").Use;
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(plan.StageA!.IsInternallyValid, Is.True);

        // Direct Core-object mutation need not signal the document revision.
        local.Graph = new FmSynthGraph([
            new FmSoundSourceNode(0, (ObjectId)9999U, 440)], 0);
        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(result.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.DestinationDependencyInvalid
            && x.DestinationObjectId == local.Id), Is.True, Message(result));
    }

    [Test]
    public void AutomaticEnvelopeSharingIsReprovedAtClickTime()
    {
        SongDocument source = new();
        NodeEnvelopeDefinition saved = new(source.AllocateObjectId(), "Saved");
        source.Add(saved);
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmEnvelopeNode(0, saved.Id)], 0));
        SongDocument destination = new();
        NodeEnvelopeDefinition identical = new(destination.AllocateObjectId(), "Local");
        destination.Add(identical);
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        Assert.That(plan.StageA!.IsInternallyValid, Is.True);
        Assert.That(plan.Effective.CopiedObjectCount, Is.EqualTo(1));

        identical.SetDisplayRange(-1, 2); // direct edit without MarkChanged
        Assert.That(plan.StageA!.IsInternallyValid, Is.True);
        FmImportStageBResult stale =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(stale.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.AutomaticMatchChanged), Is.True,
            Message(stale));

        // The user may deliberately choose a musically different local
        // Envelope. Manual provenance has no exact-equality requirement.
        FmImportObjectKey key = new(from.Discovery.SourceDocumentIdentity, saved.Id);
        plan.SetDefinitionMapping(key, FmImportMappingKind.ReuseExisting, identical.Id);
        FmImportStageBResult manual =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(manual.IsPreflightValid, Is.True, Message(manual));
    }

    [Test]
    public void SourceRevisionAndRediscoveryAreCheckedBeforePreparingAssets()
    {
        SongDocument source = new();
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        SongDocument destination = new();
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        root.Graph = new FmSynthGraph([
            new FmConstantNode(0, .7), new FmConstantNode(1, 1)], 0);
        // No revision is raised by a direct object property setter; structural
        // rediscovery is still required.
        FmImportStageBResult changed =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(changed.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.SourceChanged), Is.True,
            Message(changed));

        source.MarkChanged(affectsAudio: true);
        FmImportStageBResult stale =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(stale.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.SourceChanged), Is.True);
    }

    [Test]
    public void StageBDetectsLocalFilterPortDriftThatStageAKeepsSnapshotted()
    {
        SongDocument source = new();
        FilterDefinition saved = new(source.AllocateObjectId(), "Saved");
        source.Add(saved);
        FmSynthDefinition root = Synth(source, "Modulator",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmFilterNode(2, null, new FilterInstance(saved),
                    [new FmFilterParameterConnection(1, FilterKind.OnePole, 0,
                        "CutoffHz", 1)]),
            ], 2));
        SongDocument destination = new();
        FilterDefinition local = new(destination.AllocateObjectId(), "Local");
        destination.Add(local);
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        Assert.That(plan.Effective.Connections.Any(x =>
            x.IsActive && x.Key.PortIdentity.Contains("CutoffHz")), Is.True);
        local.ChangeStageKind(1, FilterKind.SvfLowPass);
        Assert.That(plan.StageA!.IsInternallyValid, Is.True);

        FmImportStageBResult changed =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(changed.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.FilterPortChanged), Is.True,
            Message(changed));
    }

    [Test]
    public void PendingSampleIsDecodedHashedAndFullyOwnedByTheResult()
    {
        SongDocument source = new();
        byte[] wav = Wave(3210);
        SampleDefinition sample = SampleDefinition.CreateImported(
            source.AllocateObjectId(), "Sample", "tone.wav", wav);
        source.Add(sample);
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        SongDocument destination = new();
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        uint srcRev = source.DocumentRevision, dstRev = destination.DocumentRevision;

        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(result.IsPreflightValid, Is.True, Message(result));
        Assert.That(result.PreparedSamples, Has.Count.EqualTo(1));
        FmImportPreparedSample bytes = result.PreparedSamples.Single();
        Assert.That(bytes.Source.SourceObjectId, Is.EqualTo(sample.Id));
        Assert.That(bytes.FileName, Is.EqualTo("tone.wav"));
        Assert.That(bytes.CopyEncodedBytes().ToArray(), Is.EqualTo(wav));
        Assert.That(bytes.Sha256, Is.EqualTo(sample.PendingAsset!.Sha256));
        // Returned bytes must not borrow a mutable array from the source.
        wav[0] = 0;
        Assert.That(bytes.CopyEncodedBytes().ToArray()[0], Is.EqualTo((byte)'R'));
        Assert.That(source.DocumentRevision, Is.EqualTo(srcRev));
        Assert.That(destination.DocumentRevision, Is.EqualTo(dstRev));
        Assert.That(destination.Objects, Is.Empty);
    }

    [TestCase(".hm")]
    [TestCase(".hm.json")]
    public void SavedSongAssetIsPreparedFromPackageOrExternalJsonPath(string extension)
    {
        using TemporaryDirectory dir = new();
        SongDocument original = new();
        SampleDefinition sample = SampleDefinition.CreateImported(
            original.AllocateObjectId(), "Tone", "tone.wav", Wave(3200));
        original.Add(sample);
        FmSynthDefinition root = Synth(original, "Synth",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        string path = Path.Combine(dir.Path, "song" + extension);
        SongDocumentStorage.Save(path, original);
        SongDocument loaded = SongDocumentStorage.Load(path);
        SampleDefinition saved = (SampleDefinition)loaded.Objects[sample.Id];
        Assert.That(saved.PcmData, Is.Not.Null);
        Assert.That(saved.PendingAsset, Is.Null);
        Assert.That(saved.Asset, Is.Not.Null);

        FmImportExactMatchSource from = Discover(loaded,
            (FmSynthDefinition)loaded.Objects[root.Id], path);
        FmSynthImportPlan plan = Plan(new SongDocument(), from);
        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], new SongDocument());
        Assert.That(result.IsPreflightValid, Is.True, Message(result));
        Assert.That(result.PreparedSamples, Has.Count.EqualTo(1));
        Assert.That(result.PreparedSamples[0].CopyEncodedBytes().ToArray(),
            Is.EqualTo(Wave(3200)));
    }

    [Test]
    public void ChangedHashOrDeletedEncodingFailsWithoutPartiallyPublishingAssets()
    {
        using TemporaryDirectory dir = new();
        byte[] first = Wave(3200), second = Wave(1234);
        string firstPath = Path.Combine(dir.Path, "first.wav");
        string secondPath = Path.Combine(dir.Path, "second.wav");
        File.WriteAllBytes(firstPath, first);
        File.WriteAllBytes(secondPath, second);
        SongDocument source = new();
        SampleDefinition one = new(source.AllocateObjectId(), "First",
            ExternalAssetIntegrity.CreateReference(firstPath));
        one.ReloadPersistedEncoding(one.Asset!, first);
        source.Add(one);
        SampleDefinition two = new(source.AllocateObjectId(), "Second",
            ExternalAssetIntegrity.CreateReference(secondPath));
        two.ReloadPersistedEncoding(two.Asset!, second);
        source.Add(two);
        FmSynthDefinition root = Synth(source, "Two samples",
            new FmSynthGraph([
                new FmSoundSourceNode(1, one.Id, 440),
                new FmSoundSourceNode(2, two.Id, 440),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            ], 3));
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(new SongDocument(), from);

        File.WriteAllBytes(secondPath, Wave(1111));
        FmImportStageBResult changed =
            FmSynthImportStageBPreflight.Check(plan, [from], new SongDocument());
        Assert.That(changed.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.SampleAssetChanged
            && x.Object?.SourceObjectId == two.Id), Is.True, Message(changed));
        Assert.That(changed.PreparedSamples, Is.Empty,
            "a failure must never expose an incomplete prepared asset batch");

        File.Delete(secondPath);
        FmImportStageBResult missing =
            FmSynthImportStageBPreflight.Check(plan, [from], new SongDocument());
        Assert.That(missing.Issues.Any(x =>
            x.Kind == FmImportStageBIssueKind.SampleAssetInvalid
            && x.Object?.SourceObjectId == two.Id), Is.True, Message(missing));
        Assert.That(missing.PreparedSamples, Is.Empty);
    }

    [Test]
    public void ExplicitLocalMappingExcludesUnusedMissingSampleFromAssetReads()
    {
        SongDocument source = new();
        SampleDefinition sample = new(source.AllocateObjectId(), "Broken",
            new ExternalAssetReference(Path.GetFullPath("never-created.wav")));
        source.Add(sample);
        FmSynthDefinition root = Synth(source, "Root",
            new FmSynthGraph([new FmSoundSourceNode(0, sample.Id, 440)], 0));
        SongDocument destination = new();
        FmSynthDefinition local = Synth(destination, "Local",
            new FmSynthGraph([new FmConstantNode(0, .1)], 0));
        FmImportExactMatchSource from = Discover(source, root);
        FmSynthImportPlan plan = Plan(destination, from);
        FmImportReferenceKey use = plan.Effective.References.Single(
            item => item.Use.Slot == "Source").Use;
        plan.SetReferenceOverride(use, FmImportMappingKind.ReuseExisting, local.Id);
        FmImportStageBResult result =
            FmSynthImportStageBPreflight.Check(plan, [from], destination);
        Assert.That(result.IsPreflightValid, Is.True, Message(result));
        Assert.That(result.PreparedSamples, Is.Empty);
    }

    private static byte[] Wave(short sample)
    {
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(38); // 36-byte WAVE overhead + one 16-bit sample.
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM integer
            writer.Write((short)1); // one channel
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(2);
            writer.Write(sample);
        }
        return bytes.ToArray();
    }

    private static FmSynthDefinition Synth(SongDocument song,
        string name, FmSynthGraph graph)
    {
        FmSynthDefinition definition = new(song.AllocateObjectId(), name, graph);
        song.Add(definition);
        return definition;
    }

    private static FmImportExactMatchSource Discover(
        SongDocument song, FmSynthDefinition root, string? path = null)
    {
        SongFmSynthImportSource source = new(Path.GetFullPath(path ?? "preflight.hm"),
            song, [root]);
        return new(source, FmSynthImportGraphDiscovery.Discover(source, [root.Id]));
    }

    private static FmSynthImportPlan Plan(
        SongDocument destination, params FmImportExactMatchSource[] sources)
        => FmSynthImportExactMatcher.CreatePlan(sources, destination);

    private static string Message(FmImportStageBResult result)
        => string.Join(" | ", result.Issues.Select(issue => issue.Message));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "HeresyImportStageB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
