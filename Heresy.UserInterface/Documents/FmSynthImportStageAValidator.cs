using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

public enum FmImportStageAIssueSeverity
{
    Warning,
    Error,
}

public enum FmImportStageAIssueKind
{
    MissingSource,
    MissingDestination,
    IncompatibleTarget,
    RequiredReference,
    InvalidGraph,
    DependencyCycle,
    IncompatibleFilterOverrides,
    UnavailableSample,
    InvalidName,
    ChangedConnection,
}

/// <summary>Precise plan-location anchors allow the future TreeDataGrid to
/// focus an invalid use without tying validation to transient row positions.</summary>
public sealed record FmImportStageAIssue(
    FmImportStageAIssueKind Kind, FmImportStageAIssueSeverity Severity,
    string Message, FmImportObjectKey? Object = null,
    FmImportReferenceKey? Reference = null,
    FmImportNodeKey? Node = null,
    FmImportGraphInputKey? Input = null);

/// <summary>
/// Local, interactive structural validity of the proposed imported closure.
/// This never examines outgoing references of destination objects. It is not
/// a Stage-B approval or proof of encoded Sample readability; final click-time
/// validation and asset staging are still required.
/// </summary>
public sealed class FmImportStageAResult
{
    public FmImportStageAResult(IReadOnlyList<FmImportStageAIssue> issues)
    {
        Issues = issues;
    }

    public IReadOnlyList<FmImportStageAIssue> Issues { get; }
    public bool IsInternallyValid => !Issues.Any(issue =>
        issue.Severity == FmImportStageAIssueSeverity.Error);
    public int ErrorCount => Issues.Count(issue =>
        issue.Severity == FmImportStageAIssueSeverity.Error);
    public int WarningCount => Issues.Count(issue =>
        issue.Severity == FmImportStageAIssueSeverity.Warning);
}

internal sealed record FmImportLocalObjectShape(
    ObjectId Id, SongObjectKind Kind, string RuntimeType);

internal sealed record FmImportSampleReadiness(
    bool HasDecodedPcm, bool HasEncodedAssetReference);

internal sealed record FmImportFilterOverride(
    int StageId, FilterKind Kind, long Generation,
    IReadOnlyDictionary<string, FilterParameterValue> Values);

internal sealed record FmImportFilterUseSnapshot(
    FmImportReferenceKey Use, IReadOnlyList<FmImportFilterOverride> Overrides);

/// <summary>A one-time authoring snapshot. Avoids reading the mutable songs
/// on every interactive edit. Stage B must recapture and validate the live
/// destination when Import is clicked.</summary>
internal sealed class FmImportStageAContext
{
    public Dictionary<ObjectId, FmImportLocalObjectShape> LocalObjects { get; } = [];
    public Dictionary<ObjectId, FilterDefinition> LocalFilters { get; } = [];
    public Dictionary<FmImportNodeKey, string> NodeTypes { get; } = [];
    public Dictionary<FmImportObjectKey, FmImportSampleReadiness> Samples { get; } = [];
    public Dictionary<FmImportReferenceKey, FmImportFilterUseSnapshot> FilterUses { get; } = [];

    public static FmImportStageAContext Capture(
        IReadOnlyList<FmImportExactMatchSource> sources, SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        FmImportStageAContext snapshot = new();
        foreach (SongObject local in destination.Objects.Values)
        {
            snapshot.LocalObjects.Add(local.Id,
                new(local.Id, local.Kind, local.GetType().Name));
            if (local is FilterDefinition filter)
                snapshot.LocalFilters.Add(filter.Id, CloneFilter(filter));
        }

        foreach (FmImportExactMatchSource source in sources)
        {
            string identity = source.Discovery.SourceDocumentIdentity;
            foreach (FmImportDiscoveredObject obj in source.Discovery.Objects)
            {
                if (!source.Source.Document.TryGet(obj.Key.SourceObjectId,
                    out SongObject? stored) || stored is null)
                    continue;
                if (stored is SampleDefinition sample)
                    snapshot.Samples.Add(obj.Key, new(sample.PcmData is not null,
                        sample.PendingAsset is not null || sample.Asset is not null));
                if (stored is FmSynthDefinition synth)
                {
                    foreach (FmSynthNode node in synth.Graph.Nodes)
                        snapshot.NodeTypes.Add(new FmImportNodeKey(obj.Key, node.Id),
                            node.GetType().Name);
                }
            }

            foreach (FmImportObjectReference reference in source.Discovery.ObjectReferences)
            {
                if (reference.Kind != FmImportReferenceKind.Filter
                    || !source.Source.Document.TryGet(reference.Use.Owner.SourceObjectId,
                        out SongObject? owner) || owner is null)
                    continue;

                FilterInstance? instance = null;
                if (reference.Use.NodeId is int nodeId
                    && owner is FmSynthDefinition synth)
                    instance = synth.Graph.Nodes.OfType<FmFilterNode>()
                        .FirstOrDefault(node => node.Id == nodeId)?.Configuration;
                else if (reference.Use.ToneIndex is int toneIndex)
                {
                    IReadOnlyList<ToneSpecification>? tones = owner switch
                    {
                        InstrumentDefinition instrument => instrument.ToneSpecifications,
                        ScriptInstrumentDefinition script => script.ToneSpecifications,
                        _ => null,
                    };
                    if (tones is not null && toneIndex >= 0 && toneIndex < tones.Count)
                        instance = tones[toneIndex].FilterConfiguration;
                }

                if (instance is not null)
                    snapshot.FilterUses.Add(reference.Use, CaptureOverrides(reference.Use,
                        instance));
            }
        }
        return snapshot;
    }

    private static FmImportFilterUseSnapshot CaptureOverrides(
        FmImportReferenceKey use, FilterInstance instance)
    {
        List<FmImportFilterOverride> overrides = [];
        foreach (ConfiguredFilterStage stage in instance.GetStages())
        {
            IReadOnlyDictionary<string, FilterParameterValue> values =
                instance.GetOverrides(stage.StageId);
            if (values.Count == 0)
                continue;
            overrides.Add(new(stage.StageId, stage.Kind,
                instance.GetStageGenerationForPersistence(stage.StageId),
                new Dictionary<string, FilterParameterValue>(values,
                    StringComparer.OrdinalIgnoreCase)));
        }
        return new(use, Array.AsReadOnly(overrides.ToArray()));
    }

    private static FilterDefinition CloneFilter(FilterDefinition filter)
    {
        FilterDefinition clone = new(filter.Id, filter.Name, filter.Stages);
        clone.RestoreStageIdentity(filter.NextStageIdForPersistence,
            filter.StageGenerationsForPersistence);
        return clone;
    }
}

internal static class FmSynthImportStageAValidator
{
    public static FmImportStageAResult Validate(FmSynthImportPlan plan,
        FmImportStageAContext snapshot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);

        FmImportEffectiveGraph graph = plan.Effective;
        List<FmImportStageAIssue> issues = [];

        void Error(FmImportStageAIssueKind kind, string message,
            FmImportObjectKey? obj = null, FmImportReferenceKey? use = null,
            FmImportNodeKey? node = null, FmImportGraphInputKey? input = null)
            => issues.Add(new(kind, FmImportStageAIssueSeverity.Error,
                message, obj, use, node, input));

        void Warning(FmImportStageAIssueKind kind, string message,
            FmImportObjectKey? obj = null, FmImportReferenceKey? use = null,
            FmImportNodeKey? node = null, FmImportGraphInputKey? input = null)
            => issues.Add(new(kind, FmImportStageAIssueSeverity.Warning,
                message, obj, use, node, input));

        foreach (FmImportEffectiveObject obj in graph.Objects.Where(o => o.IsCopied))
        {
            string name = plan.GetProposedName(obj.Key).Name;
            if (string.IsNullOrWhiteSpace(name))
                Error(FmImportStageAIssueKind.InvalidName,
                    $"Copied object #{obj.Key.SourceObjectId.Value} requires a name.",
                    obj.Key);
            if (!plan.DiscoveredObjects.ContainsKey(obj.Key))
                Error(FmImportStageAIssueKind.MissingSource,
                    $"Source object #{obj.Key.SourceObjectId.Value} is not discoverable.",
                    obj.Key);
            if (plan.DiscoveredObjects.TryGetValue(obj.Key,
                out FmImportDiscoveredObject? definition)
                && definition.Kind == SongObjectKind.Sample)
            {
                if (!snapshot.Samples.TryGetValue(obj.Key,
                    out FmImportSampleReadiness? sample)
                    || !sample.HasDecodedPcm || !sample.HasEncodedAssetReference)
                    Error(FmImportStageAIssueKind.UnavailableSample,
                        $"Sample #{obj.Key.SourceObjectId.Value} cannot be copied without decoded PCM and an encoded asset reference.",
                        obj.Key);
            }
        }

        // Use the effective *owner* closure, including authored disconnected
        // fragments. An inactive source issue is not an active import defect.
        foreach (FmImportObjectReference original in plan.DiscoveredReferences.Values)
        {
            FmImportReferenceKey use = original.Use;
            bool included = use.NodeId is int nodeId
                ? graph.NodeByKey.TryGetValue(new FmImportNodeKey(use.Owner, nodeId),
                    out FmImportEffectiveNode? node) && node.IsIncluded
                : graph.ObjectByKey.TryGetValue(use.Owner,
                    out FmImportEffectiveObject? owner) && owner.IsCopied;
            if (!included)
                continue;

            FmImportMapping mapping = plan.GetReferenceMapping(use);
            bool explicitlyUnassigned = mapping.Kind == FmImportMappingKind.None
                && (original.Target is null || plan.HasReferenceOverride(use)
                    || original.Target is FmImportObjectKey existingSource
                    && plan.DiscoveredObjects.ContainsKey(existingSource));

            if (mapping.Kind == FmImportMappingKind.None)
            {
                if (original.Target is not null && !explicitlyUnassigned)
                {
                    FmImportDiscoveryIssue? incompatible = plan.DiscoveryIssues
                        .FirstOrDefault(issue => issue.Use == use
                            && issue.Kind == FmImportDiscoveryIssueKind.IncompatibleObject);
                    if (incompatible is not null)
                        Error(FmImportStageAIssueKind.IncompatibleTarget,
                            incompatible.Message + " Choose a compatible target or explicitly disconnect.",
                            use: use);
                    else
                        Error(FmImportStageAIssueKind.MissingSource,
                            $"Reference {use.Slot} targets missing source object #{original.Target.Value.SourceObjectId.Value}; map it or explicitly disconnect it.",
                            use: use);
                }
                else if (original.Kind == FmImportReferenceKind.SoundSource
                    && use.ToneIndex is not null)
                {
                    Error(FmImportStageAIssueKind.RequiredReference,
                        $"Instrument tone {use.ToneIndex.Value} requires a sound source.",
                        use: use);
                }
                continue;
            }

            if (mapping.Kind == FmImportMappingKind.ImportCopy)
            {
                if (original.Target is not FmImportObjectKey target
                    || !plan.DiscoveredObjects.TryGetValue(target,
                        out FmImportDiscoveredObject? definition))
                {
                    Error(FmImportStageAIssueKind.MissingSource,
                        $"Cannot copy missing source for {use.Slot}; select a compatible local object.",
                        use: use);
                }
                else if (!IsCompatible(original.Kind, definition.Kind,
                    definition.RuntimeType))
                {
                    Error(FmImportStageAIssueKind.IncompatibleTarget,
                        $"{use.Slot} cannot use {definition.RuntimeType} #{target.SourceObjectId.Value}.",
                        use: use);
                }
                else if (!graph.ObjectByKey.TryGetValue(target,
                    out FmImportEffectiveObject? copy) || !copy.IsCopied)
                {
                    Error(FmImportStageAIssueKind.MissingSource,
                        $"{use.Slot} requires a source copy that is absent from the effective import plan.",
                        use: use);
                }
            }
            else if (mapping.Kind == FmImportMappingKind.ReuseExisting)
            {
                if (mapping.ExistingId is not ObjectId localId
                    || !snapshot.LocalObjects.TryGetValue(localId,
                        out FmImportLocalObjectShape? local))
                {
                    Error(FmImportStageAIssueKind.MissingDestination,
                        $"{use.Slot} requires a destination object that does not exist in the plan snapshot.",
                        use: use);
                    continue;
                }
                if (!IsCompatible(original.Kind, local.Kind, local.RuntimeType))
                    Error(FmImportStageAIssueKind.IncompatibleTarget,
                        $"{use.Slot} cannot use local {local.RuntimeType} #{local.Id.Value}.",
                        use: use);

                if (original.Kind == FmImportReferenceKind.Filter
                    && snapshot.FilterUses.TryGetValue(use,
                        out FmImportFilterUseSnapshot? configuration)
                    && configuration.Overrides.Count != 0
                    && snapshot.LocalFilters.TryGetValue(localId,
                        out FilterDefinition? filter))
                    ValidateFilterOverrides(use, configuration, filter);
            }
        }

        // Every imported graph must retain its output and every Operator
        // at least one operand. Other existing Core inputs are nullable.
        foreach (FmImportDiscoveredSynthGraph synth in plan.DiscoveredSynthGraphs)
        {
            if (!graph.ObjectByKey.TryGetValue(synth.Synth,
                out FmImportEffectiveObject? owner) || !owner.IsCopied)
                continue;
            if (!graph.NodeByKey.TryGetValue(synth.OutputNode,
                out FmImportEffectiveNode? output) || !output.IsIncluded)
                Error(FmImportStageAIssueKind.InvalidGraph,
                    $"Synth #{synth.Synth.SourceObjectId.Value} has no imported output.",
                    node: synth.OutputNode);

            foreach (FmImportNodeKey node in synth.Nodes)
            {
                if (!graph.NodeByKey.TryGetValue(node,
                    out FmImportEffectiveNode? included) || !included.IsIncluded)
                    continue;
                if (snapshot.NodeTypes.GetValueOrDefault(node) == nameof(FmOperatorNode)
                    && !graph.Connections.Any(edge =>
                        edge.IsActive && edge.Key.Consumer == node))
                    Error(FmImportStageAIssueKind.InvalidGraph,
                        $"Operator #{node.NodeId} must retain at least one operand.",
                        node: node);
            }
        }

        foreach (FmImportEffectiveConnection edge in graph.Connections)
        {
            if (edge.IsActive)
            {
                if (!graph.NodeByKey.TryGetValue(edge.Key.Source,
                        out FmImportEffectiveNode? source) || !source.IsIncluded
                    || !graph.NodeByKey.TryGetValue(edge.Key.Consumer,
                        out FmImportEffectiveNode? consumer) || !consumer.IsIncluded)
                    Error(FmImportStageAIssueKind.InvalidGraph,
                        $"Active port {edge.Key.PortIdentity} has a dangling node.",
                        input: edge.Key);
            }
            else if (plan.DiscoveredConnections.TryGetValue(edge.Key,
                    out FmImportGraphConnection? authored)
                && authored.IsActive
                && graph.NodeByKey.TryGetValue(edge.Key.Consumer,
                    out FmImportEffectiveNode? consumer) && consumer.IsIncluded)
                Warning(FmImportStageAIssueKind.ChangedConnection,
                    edge.InactiveReason ?? $"Graph input {edge.Key.PortIdentity} is disconnected.",
                    input: edge.Key);
        }

        // Follow only *copied* sound-source dependencies. Reused local
        // sound sources may point back into the destination: those cross-song
        // cycles are explicitly outside Stage A and belong to Stage B.
        Dictionary<FmImportObjectKey, List<(FmImportObjectKey Target,
            FmImportReferenceKey Use)>> adjacency = [];
        foreach (FmImportObjectReference reference in plan.DiscoveredReferences.Values)
        {
            if (reference.Kind != FmImportReferenceKind.SoundSource
                || reference.Target is not FmImportObjectKey target
                || plan.GetReferenceMapping(reference.Use).Kind
                    != FmImportMappingKind.ImportCopy
                || !graph.ReferenceByUse.TryGetValue(reference.Use,
                    out FmImportEffectiveReference? use) || !use.IsActive
                || !graph.ObjectByKey.TryGetValue(target,
                    out FmImportEffectiveObject? copied) || !copied.IsCopied)
                continue;
            if (!adjacency.TryGetValue(reference.Use.Owner,
                    out List<(FmImportObjectKey, FmImportReferenceKey)>? edges))
                adjacency.Add(reference.Use.Owner, edges = []);
            edges.Add((target, reference.Use));
        }

        HashSet<FmImportObjectKey> visiting = [], visited = [];
        void Visit(FmImportObjectKey key)
        {
            if (visited.Contains(key) || !visiting.Add(key))
                return;
            if (adjacency.TryGetValue(key,
                out List<(FmImportObjectKey Target, FmImportReferenceKey Use)>? children))
                foreach ((FmImportObjectKey target, FmImportReferenceKey use) in children)
                {
                    if (visiting.Contains(target))
                        Error(FmImportStageAIssueKind.DependencyCycle,
                            $"Imported sound-source dependency cycle: #{key.SourceObjectId.Value} references #{target.SourceObjectId.Value}.",
                            use: use);
                    else
                        Visit(target);
                }
            visiting.Remove(key);
            visited.Add(key);
        }
        foreach (FmImportObjectKey root in plan.SelectedRoots)
            Visit(root);

        return new FmImportStageAResult(Array.AsReadOnly(issues.ToArray()));

        void ValidateFilterOverrides(FmImportReferenceKey use,
            FmImportFilterUseSnapshot source, FilterDefinition destinationFilter)
        {
            FilterInstance trial = new(destinationFilter);
            foreach (FmImportFilterOverride original in source.Overrides)
            {
                FilterStageDefinition? target = destinationFilter.Stages
                    .FirstOrDefault(stage => stage.StageId == original.StageId);
                if (target is null || target.Kind != original.Kind
                    || !destinationFilter.StageGenerationsForPersistence.TryGetValue(
                        original.StageId, out long generation)
                    || generation != original.Generation)
                {
                    Error(FmImportStageAIssueKind.IncompatibleFilterOverrides,
                        $"Filter {use.Slot} cannot preserve overrides for stage {original.StageId}: stage identity/kind/generation changed.",
                        use: use);
                    continue;
                }
                try
                {
                    trial.ReplaceStageOverrides(original.StageId, original.Values);
                }
                catch (Exception error) when (error is ArgumentException
                    or InvalidOperationException or KeyNotFoundException)
                {
                    Error(FmImportStageAIssueKind.IncompatibleFilterOverrides,
                        $"Filter {use.Slot} overrides for stage {original.StageId} cannot be applied: {error.Message}",
                        use: use);
                }
            }
        }
    }

    private static bool IsCompatible(FmImportReferenceKind kind,
        SongObjectKind targetKind, string runtimeType)
        => kind switch
        {
            FmImportReferenceKind.SoundSource => targetKind is
                    SongObjectKind.Sample or SongObjectKind.Instrument or
                    SongObjectKind.FmSynth
                && runtimeType is nameof(SampleDefinition)
                    or nameof(InstrumentDefinition)
                    or nameof(ScriptInstrumentDefinition)
                    or nameof(FmSynthDefinition),
            FmImportReferenceKind.Envelope => targetKind == SongObjectKind.Envelope
                && runtimeType is nameof(NodeEnvelopeDefinition)
                    or nameof(ScriptEnvelopeDefinition),
            FmImportReferenceKind.Filter => targetKind == SongObjectKind.Filter
                && runtimeType == nameof(FilterDefinition),
            _ => false,
        };
}
