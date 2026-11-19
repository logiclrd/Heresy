using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

public enum FmImportStageBIssueKind
{
    StageAInvalid,
    SourceChanged,
    MissingSource,
    MissingDestination,
    IncompatibleDestination,
    AutomaticMatchChanged,
    FilterPortChanged,
    DestinationDependencyCycle,
    DestinationDependencyInvalid,
    SampleAssetMissing,
    SampleAssetChanged,
    SampleAssetInvalid,
    DestinationChanged,
    DestinationReferenceMissing,
    DestinationReferenceTombstone,
    DestinationReferenceWrongType,
    DestinationOpaqueScriptReferences,
}

public sealed record FmImportStageBIssue(
    FmImportStageBIssueKind Kind, string Message,
    FmImportObjectKey? Object = null,
    FmImportReferenceKey? Reference = null,
    ObjectId? DestinationObjectId = null);

/// <summary>
/// A defensive, separately owned copy of the exact encoded bytes required
/// to persist a Sample independently of the original .hm or .hm.json.
/// Prepared assets are NOT published into either SongDocument.
/// </summary>
public sealed class FmImportPreparedSample
{
    private readonly byte[] _bytes;

    internal FmImportPreparedSample(FmImportObjectKey source, string fileName,
        byte[] encoded, string sha256)
    {
        Source = source;
        FileName = fileName;
        _bytes = encoded.ToArray();
        Sha256 = sha256;
    }

    public FmImportObjectKey Source { get; }
    public string FileName { get; }
    public string Sha256 { get; }
    public int Length => _bytes.Length;
    public ReadOnlyMemory<byte> CopyEncodedBytes() => _bytes.ToArray();
}

/// <summary>
/// A successful preflight is not a commit token. The future mutator must
/// recheck revision/identity and perform atomic ID reservation/rollback;
/// no destination IDs are allocated here.
/// </summary>
public sealed class FmImportStageBResult
{
    internal FmImportStageBResult(
        IReadOnlyList<FmImportStageBIssue> issues,
        IReadOnlyList<FmImportPreparedSample> samples,
        IReadOnlyList<FmImportStageBIssue>? warnings = null)
    {
        Issues = issues;
        Warnings = warnings ?? Array.Empty<FmImportStageBIssue>();
        PreparedSamples = issues.Count == 0 ? samples : [];
    }

    public IReadOnlyList<FmImportStageBIssue> Issues { get; }
    /// <summary>Non-blocking limits of validation, e.g. opaque script tokens.
    /// They are not falsely reported as complete reference validation.</summary>
    public IReadOnlyList<FmImportStageBIssue> Warnings { get; }
    public IReadOnlyList<FmImportPreparedSample> PreparedSamples { get; }
    public bool IsPreflightValid => Issues.Count == 0;
}

/// <summary>
/// Explicit click-time preflight foundation; unlike Stage A this reads the
/// LIVE destination, walks its sound-source graph and verifies encoded
/// Samples. This is independent of the GUI and never mutates either song.
///
/// TODO: Future transactional commit must guard against edits after preflight,
/// allocate fresh IDs, and provide complete song-wide non-audio validation.
/// </summary>
public static class FmSynthImportStageBPreflight
{
    private readonly record struct PlannedSource(
        FmImportObjectKey? Copy, ObjectId? Local)
    {
        public static PlannedSource Imported(FmImportObjectKey key) => new(key, null);
        public static PlannedSource Existing(ObjectId id) => new(null, id);

        public override string ToString() => Copy is FmImportObjectKey copy
            ? $"imported {copy.SourceDocumentIdentity} #{copy.SourceObjectId.Value}"
            : $"local #{Local?.Value}";
    }

    public static FmImportStageBResult Check(
        FmSynthImportPlan plan, IReadOnlyList<FmImportExactMatchSource> sources,
        SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);

        List<FmImportStageBIssue> issues = [];
        List<FmImportStageBIssue> warnings = [];
        List<FmImportPreparedSample> prepared = [];

        void Add(FmImportStageBIssueKind kind, string message,
            FmImportObjectKey? obj = null, FmImportReferenceKey? use = null,
            ObjectId? local = null)
            => issues.Add(new(kind, message, obj, use, local));

        Dictionary<string, SongDocument> sourceDocuments =
            new(StringComparer.Ordinal);
        foreach (FmImportExactMatchSource item in sources)
        {
            if (item is null || item.Source is null || item.Discovery is null
                || !sourceDocuments.TryAdd(item.Discovery.SourceDocumentIdentity,
                    item.Source.Document)
                || !string.Equals(Path.GetFullPath(item.Source.Path),
                    item.Discovery.SourceDocumentIdentity, StringComparison.Ordinal))
            {
                Add(FmImportStageBIssueKind.SourceChanged,
                    "The selected import-source set has changed; reopen the plan.");
                continue;
            }
            string identity = item.Discovery.SourceDocumentIdentity;
            // A direct authoring-property edit may leave DocumentRevision and
            // the graph's discovery shape untouched. Compare complete persisted
            // content, without calling the ordinary pruning song serializer.
            if (!plan.SourceDocumentFingerprints.TryGetValue(identity,
                    out string? originalFingerprint)
                || !MatchesFingerprint(item.Source.Document, originalFingerprint))
                Add(FmImportStageBIssueKind.SourceChanged,
                    $"Import source '{identity}' has changed authored content after the plan was created.",
                    new FmImportObjectKey(identity, ObjectId.None));
            // Never trust a different loaded source with the same numeric IDs.
            // The root/discovery structure and saved revision must still match.
            if (!plan.SourceDocumentRevisions.TryGetValue(identity,
                    out uint revision)
                || revision != item.Source.Document.DocumentRevision)
                Add(FmImportStageBIssueKind.SourceChanged,
                    $"Import source '{identity}' was modified after planning; reopen the plan.");
            try
            {
                FmImportDiscoveredGraph refreshed =
                    FmSynthImportGraphDiscovery.Discover(item.Source,
                        item.Discovery.SelectedSynthRoots.Select(key => key.SourceObjectId));
                if (!SameDiscovery(refreshed, item.Discovery))
                    Add(FmImportStageBIssueKind.SourceChanged,
                        $"Import source '{identity}' no longer matches its discovered graph.");
            }
            catch (Exception error) when (error is InvalidOperationException
                or ArgumentException or NotSupportedException)
            {
                Add(FmImportStageBIssueKind.SourceChanged,
                    $"Import source '{identity}' cannot be rediscovered: {error.Message}");
            }
        }

        if (sourceDocuments.Count != plan.SourceDocumentRevisions.Count
            || plan.SourceDocumentRevisions.Keys.Any(identity =>
                !sourceDocuments.ContainsKey(identity)))
            Add(FmImportStageBIssueKind.SourceChanged,
                "The import-source documents do not match this plan.");

        // Final-click checks must also catch direct edits to existing local
        // object parameters, names, tree placements and tombstones. Checking
        // the whole snapshot is deliberately conservative: a changed local
        // song requires refreshed mapping choices before import.
        if (plan.DestinationDocumentFingerprint is not string targetFingerprint
            || !MatchesFingerprint(destination, targetFingerprint))
            Add(FmImportStageBIssueKind.DestinationChanged,
                "The destination song changed after the import plan was opened; refresh it before importing.");

        // Audio cycles are handled below. Non-audio Pattern/Sequence, tree,
        // Instrument and Filter reference integrity requires its own sweep.
        // Do not claim ScriptPattern/ScriptSequence references are audited
        // when only their opaque script source is available.
        AuditDestinationReferences(destination, Add,
            issue => warnings.Add(issue));

        // Stage A is rechecked against the actual destination at click time.
        // The cached StageA property is intentionally not used as authority.
        FmImportStageAResult freshStageA =
            FmSynthImportStageAValidator.Validate(plan,
                FmImportStageAContext.Capture(sources, destination));
        foreach (FmImportStageAIssue issue in freshStageA.Issues.Where(
            issue => issue.Severity == FmImportStageAIssueSeverity.Error))
            Add(FmImportStageBIssueKind.StageAInvalid,
                issue.Message, issue.Object, issue.Reference);

        // The automatic orange highlight was a historical *proof*. Recheck
        // its premise on the live destination before allowing reuse.
        foreach (FmImportEffectiveObject obj in plan.Effective.Objects)
        {
            if (obj.Mapping.Kind != FmImportMappingKind.ReuseExisting
                || obj.Mapping.Provenance != FmImportMappingProvenance.AutomaticExactMatch
                || obj.Mapping.ExistingId is not ObjectId localId
                || !obj.IsReferenced)
                continue;
            if (!sourceDocuments.TryGetValue(obj.Key.SourceDocumentIdentity,
                    out SongDocument? source)
                || !source.TryGet(obj.Key.SourceObjectId, out SongObject? original)
                || original is null
                || !destination.TryGet(localId, out SongObject? local)
                || local is null
                || !FmSynthImportExactMatcher.StrictlyEqual(original, local))
                Add(FmImportStageBIssueKind.AutomaticMatchChanged,
                    $"Automatically reused object #{localId.Value} is no longer an exact match; reopen the plan or map it manually.",
                    obj.Key, local: localId);
        }

        // Stage A used the captured local Filter port schema. A local Filter
        // may have changed since then, even without a marked revision.
        foreach (FmImportEffectiveReference reference in plan.Effective.References
            .Where(r => r.IsActive && r.Mapping.Kind == FmImportMappingKind.ReuseExisting
                && r.Use.Slot == "Filter"))
        {
            if (reference.Mapping.ExistingId is not ObjectId localId
                || !destination.TryGet(localId, out SongObject? target)
                || target is not FilterDefinition localFilter)
                continue; // Fresh Stage A already diagnoses a missing/wrong type.

            FmImportFilterPortSchema ports = FmImportFilterPortSchema.Capture(localFilter);
            foreach (FmImportEffectiveConnection connection in
                plan.Effective.Connections.Where(c =>
                    c.IsActive && c.Key.Consumer.SourceSynth == reference.Use.Owner
                    && c.Key.Consumer.NodeId == reference.Use.NodeId
                    && c.Key.PortIdentity != "audio"))
            {
                FmImportFilterPort? currentPort = ports.Ports.FirstOrDefault(port =>
                    string.Equals(port.Identity, connection.DestinationPortIdentity,
                        StringComparison.OrdinalIgnoreCase));
                if (currentPort is null)
                    Add(FmImportStageBIssueKind.FilterPortChanged,
                        $"Destination Filter #{localId.Value} no longer has modulation port '{connection.Key.PortIdentity}'.",
                        use: reference.Use, local: localId);
                else if (connection.DestinationPortIndex != currentPort.Index)
                    Add(FmImportStageBIssueKind.FilterPortChanged,
                        $"Destination Filter #{localId.Value} moved modulation port '{connection.Key.PortIdentity}' from planned index {connection.DestinationPortIndex} to {currentPort.Index}; refresh the import plan before applying it.",
                        use: reference.Use, local: localId);
            }
        }

        // Explicit references are validated against *live* destination objects
        // and the actual selected mapping. A different musical sound is valid.
        foreach (FmImportEffectiveReference reference in plan.Effective.References
            .Where(r => r.IsActive && r.Mapping.Kind == FmImportMappingKind.ReuseExisting))
        {
            if (reference.Mapping.ExistingId is not ObjectId id
                || !destination.TryGet(id, out SongObject? local)
                || local is null)
            {
                Add(FmImportStageBIssueKind.MissingDestination,
                    $"Mapped local target of {reference.Use.Slot} is missing.",
                    use: reference.Use);
                continue;
            }
            bool compatible = reference.Use.Slot switch
            {
                "Source" => IsSoundSource(local),
                "Envelope" or "VolumeEnvelope" or "PitchEnvelope"
                    or "PanningEnvelope" or "FilterEnvelope"
                    => local is NodeEnvelopeDefinition or ScriptEnvelopeDefinition,
                "Filter" => local is FilterDefinition,
                _ => false,
            };
            if (!compatible)
                Add(FmImportStageBIssueKind.IncompatibleDestination,
                    $"Local #{id.Value} has incompatible type {local.GetType().Name} for {reference.Use.Slot}.",
                    use: reference.Use, local: id);
        }

        // Two disjoint namespaces: fresh copies are NOT the live song objects
        // that happen to share their numeric source IDs. All existing sources
        // are walked, not just the direct mapped target.
        Dictionary<PlannedSource, List<PlannedSource>> adjacency = [];
        foreach (FmImportEffectiveObject obj in plan.Effective.Objects.Where(o => o.IsCopied))
        {
            PlannedSource key = PlannedSource.Imported(obj.Key);
            adjacency[key] = [];
        }
        foreach (SongObject local in destination.Objects.Values)
        {
            if (!IsSoundSource(local))
                continue;
            PlannedSource owner = PlannedSource.Existing(local.Id);
            adjacency[owner] = [];
            foreach (ObjectId target in SoundTargets(local))
            {
                if (target.IsNone)
                    continue;
                if (!destination.TryGet(target, out SongObject? candidate)
                    || candidate is null || !IsSoundSource(candidate))
                {
                    Add(FmImportStageBIssueKind.DestinationDependencyInvalid,
                        $"Existing sound source #{local.Id.Value} references missing or non-sound object #{target.Value}.",
                        local: local.Id);
                    continue;
                }
                adjacency[owner].Add(PlannedSource.Existing(target));
            }
        }

        foreach (FmImportEffectiveReference reference in plan.Effective.References
            .Where(r => r.IsActive && r.Use.Slot == "Source"))
        {
            if (!adjacency.TryGetValue(PlannedSource.Imported(reference.Use.Owner),
                out List<PlannedSource>? edges))
                continue;
            if (reference.Mapping.Kind == FmImportMappingKind.ReuseExisting
                && reference.Mapping.ExistingId is ObjectId id)
            {
                PlannedSource target = PlannedSource.Existing(id);
                if (adjacency.ContainsKey(target))
                    edges.Add(target);
            }
            else if (reference.Mapping.Kind == FmImportMappingKind.ImportCopy
                && reference.OriginalTarget is FmImportObjectKey target)
            {
                PlannedSource next = PlannedSource.Imported(target);
                if (adjacency.ContainsKey(next))
                    edges.Add(next);
            }
        }

        HashSet<PlannedSource> visiting = [], visited = [];
        void Walk(PlannedSource key)
        {
            if (visited.Contains(key) || !visiting.Add(key))
                return;
            foreach (PlannedSource target in adjacency[key])
            {
                if (visiting.Contains(target))
                    Add(FmImportStageBIssueKind.DestinationDependencyCycle,
                        $"Combined destination sound-source cycle: {key} references {target}.",
                        key.Copy, local: key.Local);
                else if (adjacency.ContainsKey(target))
                    Walk(target);
            }
            visiting.Remove(key);
            visited.Add(key);
        }
        foreach (PlannedSource key in adjacency.Keys)
            Walk(key);

        // Final acceptance must verify encoded files for *both* copied and
        // automatically reused Sample dependencies. Otherwise an on-disk
        // source/destination asset could be replaced after the song was loaded
        // and its cached PCM hash would still describe the old buffer.
        // Unreferenced samples, including explicitly overridden source uses,
        // are never read. No partial prepared-byte bundle escapes on failure.
        if (issues.Count == 0)
        {
            HashSet<ObjectId> verifiedLocalSamples = [];

            void VerifyAsset(SampleDefinition sample, string role,
                FmImportObjectKey? obj, ObjectId? localId, bool copy)
            {
                try
                {
                    byte[] bytes;
                    string fileName;
                    string? expectedHash;
                    if (sample.PendingAsset is PendingSampleAsset pending)
                    {
                        bytes = pending.Bytes.ToArray();
                        fileName = pending.FileName;
                        expectedHash = pending.Sha256;
                    }
                    else if (sample.Asset is ExternalAssetReference asset)
                    {
                        using Stream stream = ExternalAssetIntegrity.OpenRead(asset.FullPath);
                        using MemoryStream buffer = new();
                        stream.CopyTo(buffer);
                        bytes = buffer.ToArray();
                        fileName = Path.GetFileName(asset.FullPath);
                        expectedHash = asset.Sha256;
                    }
                    else
                    {
                        Add(FmImportStageBIssueKind.SampleAssetMissing,
                            $"{role} has no encoded representation.",
                            obj, local: localId);
                        return;
                    }

                    // Recompute the *encoded file's* digest from bytes freshly
                    // read at Import-click time. Do not confuse this with
                    // SamplePcmData.Sha256 (immutable decoded audio).
                    string hash = Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant();
                    if (expectedHash is not null
                        && !string.Equals(hash, expectedHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Add(FmImportStageBIssueKind.SampleAssetChanged,
                            $"{role} encoded SHA-256 has changed.",
                            obj, local: localId);
                        return;
                    }

                    // Decode creates new immutable PCM with a newly computed
                    // hash from the *current* file. Check the digest first,
                    // then every PCM bit if hashes match. A matching digest
                    // by itself never authorizes an import or Sample reuse.
                    SamplePcmData decoded = SampleAudioCodec.Decode(bytes, fileName);
                    if (sample.PcmData is not SamplePcmData original
                        || !decoded.HasSameAudio(original))
                    {
                        Add(FmImportStageBIssueKind.SampleAssetInvalid,
                            $"{role} encoded PCM does not match the loaded audio (content, channels, rate or frames).",
                            obj, local: localId);
                        return;
                    }
                    if (copy && obj is FmImportObjectKey sourceKey)
                        prepared.Add(new(sourceKey, fileName, bytes, hash));
                }
                catch (Exception error) when (error is IOException
                    or UnauthorizedAccessException or ArgumentException
                    or NotSupportedException or InvalidOperationException
                    or OverflowException)
                {
                    Add(FmImportStageBIssueKind.SampleAssetInvalid,
                        $"{role} cannot be verified: {error.Message}",
                        obj, local: localId);
                }
            }

            foreach (FmImportEffectiveObject obj in plan.Effective.Objects.Where(o =>
                (o.IsCopied || o.IsReferenced)
                && plan.DiscoveredObjects[o.Key].Kind == SongObjectKind.Sample))
            {
                if (!sourceDocuments.TryGetValue(obj.Key.SourceDocumentIdentity,
                        out SongDocument? original)
                    || !original.TryGet(obj.Key.SourceObjectId,
                        out SongObject? stored) || stored is not SampleDefinition sample)
                {
                    Add(FmImportStageBIssueKind.MissingSource,
                        $"Referenced Sample #{obj.Key.SourceObjectId.Value} is unavailable.",
                        obj.Key);
                    continue;
                }
                VerifyAsset(sample,
                    $"Source Sample #{obj.Key.SourceObjectId.Value}",
                    obj.Key, null, obj.IsCopied);

                if (obj.Mapping.Kind == FmImportMappingKind.ReuseExisting
                    && obj.Mapping.Provenance ==
                        FmImportMappingProvenance.AutomaticExactMatch
                    && obj.Mapping.ExistingId is ObjectId localId
                    && verifiedLocalSamples.Add(localId))
                {
                    if (!destination.TryGet(localId, out SongObject? existing)
                        || existing is not SampleDefinition localSample)
                        Add(FmImportStageBIssueKind.MissingDestination,
                            $"Automatically reused destination Sample #{localId.Value} is missing.",
                            obj.Key, local: localId);
                    else
                        VerifyAsset(localSample,
                            $"Destination Sample #{localId.Value}",
                            obj.Key, localId, copy: false);
                }
            }
        }

        return new FmImportStageBResult(
            Array.AsReadOnly(issues.ToArray()),
            Array.AsReadOnly(prepared.ToArray()),
            Array.AsReadOnly(warnings.ToArray()));
    }

    private static bool MatchesFingerprint(SongDocument song, string expected)
    {
        try
        {
            return string.Equals(FmSynthImportAuthoringFingerprint.Capture(song),
                expected, StringComparison.Ordinal);
        }
        catch (Exception failure) when (failure is ArgumentException
            or InvalidOperationException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // Invalid authored state is never a reason to bypass preflight.
            return false;
        }
    }

    /// <summary>
    /// Read-only whole-destination explicit reference audit. Opaque source
    /// scripts cannot be safely resolved by Core without a script analyzer;
    /// report that limitation as a warning rather than claiming proof.
    /// </summary>
    private static void AuditDestinationReferences(
        SongDocument destination,
        Action<FmImportStageBIssueKind, string, FmImportObjectKey?,
            FmImportReferenceKey?, ObjectId?> error,
        Action<FmImportStageBIssue> warn)
    {
        SongReferenceAnalysis analysis = SongReferenceAnalyzer.Analyze(destination);
        foreach (SongReference reference in analysis.References)
        {
            ObjectId id = reference.TargetId;
            if (!destination.TryGet(id, out SongObject? target) || target is null)
            {
                if (destination.Tombstones.ContainsKey(id))
                    error(FmImportStageBIssueKind.DestinationReferenceTombstone,
                        $"Existing {reference.Kind} reference #{id.Value} points to a tombstoned object.",
                        null, null, reference.SourceObjectId ?? id);
                else
                    error(FmImportStageBIssueKind.DestinationReferenceMissing,
                        $"Existing {reference.Kind} reference #{id.Value} has no destination object.",
                        null, null, reference.SourceObjectId ?? id);
                continue;
            }

            bool compatible = reference.Kind switch
            {
                SongReferenceKind.RootSequence =>
                    target is DataSequenceDefinition or ScriptSequenceDefinition,
                SongReferenceKind.SequencePattern =>
                    target is DataPatternDefinition or ScriptPatternDefinition,
                SongReferenceKind.PatternNoteSource
                    or SongReferenceKind.InstrumentSource
                    or SongReferenceKind.FmSynthSoundSource =>
                    IsSoundSource(target),
                SongReferenceKind.PatternNoteFilter
                    or SongReferenceKind.InstrumentToneFilter =>
                    target is FilterDefinition,
                SongReferenceKind.InstrumentVolumeEnvelope
                    or SongReferenceKind.InstrumentPitchEnvelope
                    or SongReferenceKind.InstrumentPanningEnvelope
                    or SongReferenceKind.InstrumentFilterEnvelope
                    or SongReferenceKind.FmSynthEnvelope =>
                    target is NodeEnvelopeDefinition or ScriptEnvelopeDefinition,
                SongReferenceKind.Tree or SongReferenceKind.ScriptObject => true,
                _ => false,
            };
            if (!compatible)
                error(FmImportStageBIssueKind.DestinationReferenceWrongType,
                    $"Existing {reference.Kind} reference #{id.Value} targets {target.GetType().Name}, which has the wrong kind.",
                    null, null, reference.SourceObjectId ?? id);
        }

        // SongReferenceAnalyzer has no FM Filter slot kind; these per-node
        // configurations must still target present Filter definitions.
        foreach (FmSynthDefinition synth in destination.Objects.Values
            .OfType<FmSynthDefinition>())
            foreach (FmFilterNode filter in synth.Graph.Nodes.OfType<FmFilterNode>())
            {
                if (filter.Configuration is not FilterInstance instance)
                    continue;
                ObjectId id = instance.DefinitionId;
                if (!destination.TryGet(id, out SongObject? target)
                    || target is not FilterDefinition)
                    error(destination.Tombstones.ContainsKey(id)
                            ? FmImportStageBIssueKind.DestinationReferenceTombstone
                            : FmImportStageBIssueKind.DestinationReferenceWrongType,
                        $"Existing Synth #{synth.Id.Value}, Filter node #{filter.Id}, uses unavailable Filter definition #{id.Value}.",
                        null, null, synth.Id);
            }

        if (analysis.HasOpaqueScriptReferences)
            warn(new FmImportStageBIssue(
                FmImportStageBIssueKind.DestinationOpaqueScriptReferences,
                "The destination contains scripted Pattern/Sequence references that the Core reference analyzer cannot resolve; non-script explicit references were audited."));
    }

    private static bool SameDiscovery(FmImportDiscoveredGraph actual,
        FmImportDiscoveredGraph recorded)
        => actual.SourceDocumentIdentity == recorded.SourceDocumentIdentity
           && actual.SelectedSynthRoots.SequenceEqual(recorded.SelectedSynthRoots)
           && actual.Objects.SequenceEqual(recorded.Objects)
           && actual.Connections.SequenceEqual(recorded.Connections)
           && actual.ObjectReferences.SequenceEqual(recorded.ObjectReferences)
           && actual.Issues.SequenceEqual(recorded.Issues)
           && actual.SynthGraphs.Count == recorded.SynthGraphs.Count
           && actual.SynthGraphs.Zip(recorded.SynthGraphs).All(pair =>
               pair.First.Synth == pair.Second.Synth
               && pair.First.OutputNode == pair.Second.OutputNode
               && pair.First.Nodes.SequenceEqual(pair.Second.Nodes)
               && pair.First.PresentationRoots.SequenceEqual(pair.Second.PresentationRoots));

    private static bool IsSoundSource(SongObject obj)
        => obj is FmSynthDefinition or InstrumentDefinition
            or ScriptInstrumentDefinition or SampleDefinition;

    private static IEnumerable<ObjectId> SoundTargets(SongObject obj)
        => obj switch
        {
            FmSynthDefinition synth => synth.Graph.Nodes
                .OfType<FmSoundSourceNode>()
                .Select(node => node.SourceId),
            InstrumentDefinition instrument => instrument.ToneSpecifications
                .Select(tone => tone.SourceId),
            ScriptInstrumentDefinition instrument => instrument.ToneSpecifications
                .Select(tone => tone.SourceId),
            _ => [],
        };
}
