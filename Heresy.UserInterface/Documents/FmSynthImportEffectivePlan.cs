using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>A *snapshot* of a local Filter's semantic graph ports. It is
/// deliberately independent of later mutation of the destination document.
/// Stage ID, kind and generation must all agree; same parameter labels alone
/// never authorize rebinding an FM modulation wire.</summary>
public sealed record FmImportFilterPort(int Index, string Identity, string Label);

public sealed record FmImportFilterPortSchema(ObjectId DefinitionId,
    IReadOnlyList<FmImportFilterPort> Ports)
{
    public static FmImportFilterPortSchema Capture(FilterDefinition filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        FilterInstance instance = new(filter);
        List<FmImportFilterPort> ports =
            [new FmImportFilterPort(0, "audio", "Audio")];
        foreach (ConfiguredFilterStage stage in instance.GetStages())
        {
            long generation = instance.GetStageGenerationForPersistence(stage.StageId);
            foreach (FilterParameterDescriptor parameter in stage.Parameters.Parameters)
            {
                // Only numeric nonstructural FM-modulatable parameters are
                // graph ports in the current Core definition.
                if (!parameter.IsModulatable ||
                    parameter.Kind != FilterParameterValueKind.Number)
                    continue;
                ports.Add(new FmImportFilterPort(ports.Count,
                    $"filter:{stage.StageId}:{(int)stage.Kind}:{generation}:{parameter.Name}",
                    $"Stage {stage.StageId}: {parameter.Name}"));
            }
        }
        return new(filter.Id, Array.AsReadOnly(ports.ToArray()));
    }
}

public sealed record FmImportEffectiveObject(
    FmImportObjectKey Key, bool IsCopied, bool IsReferenced,
    FmImportMapping Mapping);

public sealed record FmImportEffectiveNode(FmImportNodeKey Key, bool IsIncluded);

public sealed record FmImportEffectiveConnection(
    FmImportGraphInputKey Key,
    bool IsActive, string? DestinationPortIdentity, int? DestinationPortIndex,
    string? InactiveReason);

public sealed record FmImportEffectiveReference(
    FmImportReferenceKey Use, FmImportObjectKey? OriginalTarget,
    FmImportMapping Mapping, bool IsActive, string? InactiveReason);

/// <summary>
/// Recalculated projection of the decisions over the discovered source graphs.
/// Reports expected copy/reuse/liveness and semantic Filter input translation.
/// It is NOT a Stage-A or destination-wide validity judgment, a prepared
/// object graph, or authorization to copy encoded Sample assets.
/// </summary>
public sealed class FmImportEffectiveGraph
{
    public FmImportEffectiveGraph(
        IReadOnlyList<FmImportEffectiveObject> objects,
        IReadOnlyList<FmImportEffectiveNode> nodes,
        IReadOnlyList<FmImportEffectiveConnection> connections,
        IReadOnlyList<FmImportEffectiveReference> references)
    {
        Objects = objects;
        Nodes = nodes;
        Connections = connections;
        References = references;
        ObjectByKey = objects.ToDictionary(item => item.Key);
        NodeByKey = nodes.ToDictionary(item => item.Key);
        ConnectionByKey = connections.ToDictionary(item => item.Key);
        ReferenceByUse = references.ToDictionary(item => item.Use);
    }

    public IReadOnlyList<FmImportEffectiveObject> Objects { get; }
    public IReadOnlyList<FmImportEffectiveNode> Nodes { get; }
    public IReadOnlyList<FmImportEffectiveConnection> Connections { get; }
    public IReadOnlyList<FmImportEffectiveReference> References { get; }
    public IReadOnlyDictionary<FmImportObjectKey, FmImportEffectiveObject> ObjectByKey { get; }
    public IReadOnlyDictionary<FmImportNodeKey, FmImportEffectiveNode> NodeByKey { get; }
    public IReadOnlyDictionary<FmImportGraphInputKey, FmImportEffectiveConnection> ConnectionByKey { get; }
    public IReadOnlyDictionary<FmImportReferenceKey, FmImportEffectiveReference> ReferenceByUse { get; }
    public int CopiedObjectCount => Objects.Count(item => item.IsCopied);
    public int ReusedReferenceCount => References.Count(item =>
        item.IsActive && item.Mapping.Kind == FmImportMappingKind.ReuseExisting);
    public int InactiveConnectionCount => Connections.Count(item => !item.IsActive);
}

/// <summary>Pure planning over source identities and captured local Filter
/// schemas, not over mutable source/destination SongDocuments.</summary>
internal static class FmSynthImportEffectivePlanner
{
    public static FmImportEffectiveGraph Calculate(FmSynthImportPlan plan)
    {
        var objects = plan.DiscoveredObjects;
        var graphs = plan.DiscoveredSynthGraphs.ToDictionary(graph => graph.Synth);
        var connections = plan.DiscoveredConnections;
        var references = plan.DiscoveredReferences;
        var portsByFilter = plan.DestinationFilterPorts;

        HashSet<FmImportObjectKey> copied = [];
        HashSet<FmImportObjectKey> referenced = [];
        HashSet<FmImportNodeKey> liveNodes = [];
        HashSet<FmImportGraphInputKey> liveInputs = [];
        HashSet<FmImportReferenceKey> liveReferences = [];
        Dictionary<FmImportGraphInputKey, string> stoppedInputs = [];
        Dictionary<FmImportGraphInputKey, FmImportFilterPort> boundPorts = [];

        var edgesByConsumer = connections.Values
            .GroupBy(edge => edge.Target).ToDictionary(g => g.Key, g => g.ToArray());
        var refsByObject = references.Values
            .GroupBy(reference => reference.Use.Owner)
            .ToDictionary(g => g.Key, g => g.ToArray());

        FmImportObjectReference? FilterUse(FmImportNodeKey filter)
            => refsByObject.TryGetValue(filter.SourceSynth,
                    out FmImportObjectReference[]? uses)
                ? uses.FirstOrDefault(item =>
                    item.Use.NodeId == filter.NodeId && item.Use.Slot == "Filter"
                    && item.Kind == FmImportReferenceKind.Filter)
                : null;

        bool CanConnect(FmImportGraphInputKey key,
            FmImportGraphConnection edge, out string? why,
            out FmImportFilterPort? localPort)
        {
            localPort = null;
            why = null;
            if (!edge.IsActive)
                why = "Inactive stale Filter connection (authoring metadata only).";
            else if (plan.IsInputDisconnected(key))
                why = "Input explicitly disconnected in import plan.";
            else if (plan.IsNodeExcluded(edge.Source))
                why = "Input node excluded from import.";
            else if (plan.IsNodeExcluded(edge.Target))
                why = "Consumer node excluded from import.";
            if (why is not null)
                return false;

            // All live Filter edges have a semantic identity. Audio remains
            // available when the Filter is mapped to None; modulation does not.
            FmImportObjectReference? filterUse = FilterUse(edge.Target);
            if (filterUse is null)
                return true;
            FmImportMapping mapping = plan.GetReferenceMapping(filterUse.Use);
            if (edge.PortIdentity == "audio")
            {
                localPort = new FmImportFilterPort(0, "audio", "Audio");
                return true;
            }
            if (mapping.Kind == FmImportMappingKind.None)
            {
                why = "Not imported: this Filter is unassigned.";
                return false;
            }
            if (mapping.Kind != FmImportMappingKind.ReuseExisting)
                return true; // The copied source Filter retains its exact topology.

            if (mapping.ExistingId is not ObjectId localId ||
                !portsByFilter.TryGetValue(localId,
                    out FmImportFilterPortSchema? schema))
            {
                why = "Not imported: destination Filter port schema is unavailable.";
                return false;
            }
            localPort = schema.Ports.FirstOrDefault(port =>
                string.Equals(port.Identity, edge.PortIdentity,
                    StringComparison.OrdinalIgnoreCase));
            if (localPort is null)
            {
                why = $"Not imported: destination Filter #{localId.Value} has no matching {edge.PortLabel} port (stage identity, kind, generation and parameter must agree).";
                return false;
            }
            return true;
        }

        void VisitReference(FmImportObjectReference reference)
        {
            if (!liveReferences.Add(reference.Use))
                return;
            FmImportMapping mapping = plan.GetReferenceMapping(reference.Use);
            if (reference.Target is not FmImportObjectKey target
                || !objects.ContainsKey(target)
                || mapping.Kind == FmImportMappingKind.None)
                return;
            if (mapping.Kind == FmImportMappingKind.ImportCopy)
            {
                referenced.Add(target);
                VisitObject(target);
            }
            else if (!plan.HasReferenceOverride(reference.Use))
            {
                // The source definition is still shown as a reused entity
                // when this is its inherited definition-wide mapping.
                referenced.Add(target);
            }
        }

        void VisitNode(FmImportNodeKey node)
        {
            if (plan.IsNodeExcluded(node) || !plan.DiscoveredNodes.Contains(node)
                || !liveNodes.Add(node))
                return;

            if (edgesByConsumer.TryGetValue(node,
                    out FmImportGraphConnection[]? inputs))
            {
                foreach (FmImportGraphConnection input in inputs)
                {
                    FmImportGraphInputKey key = FmImportGraphInputKey.From(input);
                    if (!CanConnect(key, input, out string? why,
                            out FmImportFilterPort? port))
                    {
                        stoppedInputs[key] = why!;
                        continue;
                    }
                    if (port is not null)
                        boundPorts[key] = port;
                    liveInputs.Add(key);
                    VisitNode(input.Source);
                }
            }
            if (refsByObject.TryGetValue(node.SourceSynth,
                    out FmImportObjectReference[]? uses))
            {
                foreach (FmImportObjectReference reference in uses)
                    if (reference.Use.NodeId == node.NodeId)
                        VisitReference(reference);
            }
        }

        void VisitObject(FmImportObjectKey key)
        {
            if (!objects.ContainsKey(key) || !copied.Add(key))
                return;
            if (graphs.TryGetValue(key, out FmImportDiscoveredSynthGraph? graph))
            {
                VisitNode(graph.OutputNode);
                // Source-authored disconnected components are retained,
                // independently of connectivity to the designated output.
                foreach (FmImportNodeKey root in graph.PresentationRoots)
                    VisitNode(root);
                foreach (FmImportNodeKey preserved in plan.PreservedDisconnectedNodes)
                    if (preserved.SourceSynth == key)
                        VisitNode(preserved);
            }
            else if (refsByObject.TryGetValue(key,
                         out FmImportObjectReference[]? referencesForObject))
            {
                foreach (FmImportObjectReference reference in referencesForObject)
                    if (reference.Use.NodeId is null)
                        VisitReference(reference);
            }
        }

        foreach (FmImportObjectKey root in plan.SelectedRoots)
            VisitObject(root);

        FmImportEffectiveObject[] objectsResult = objects.Values.Select(item =>
            new FmImportEffectiveObject(item.Key, copied.Contains(item.Key),
                referenced.Contains(item.Key) || copied.Contains(item.Key),
                plan.GetDefinitionMapping(item.Key))).ToArray();

        FmImportEffectiveNode[] nodesResult = plan.DiscoveredNodes.Select(key =>
            new FmImportEffectiveNode(key, liveNodes.Contains(key))).ToArray();

        FmImportEffectiveConnection[] connectionsResult =
            connections.Select(pair =>
            {
                (FmImportGraphInputKey key, FmImportGraphConnection edge) = pair;
                bool active = liveInputs.Contains(key) &&
                    liveNodes.Contains(edge.Source) && liveNodes.Contains(edge.Target);
                string? why = null;
                if (!active)
                {
                    if (stoppedInputs.TryGetValue(key, out string? reason))
                        why = reason;
                    else if (!liveNodes.Contains(edge.Target))
                        why = "Consumer node not included in import.";
                    else if (!liveNodes.Contains(edge.Source))
                        why = "Source node not included in import.";
                    else
                        why = edge.IsActive ? "Input not included." :
                            "Inactive stale Filter connection (authoring metadata only).";
                }
                boundPorts.TryGetValue(key, out FmImportFilterPort? bound);
                return new FmImportEffectiveConnection(key, active,
                    active ? bound?.Identity ?? edge.PortIdentity : null,
                    active ? bound?.Index ?? edge.TargetInputIndex : null, why);
            }).ToArray();

        FmImportEffectiveReference[] referencesResult = references.Values.Select(reference =>
        {
            bool active = liveReferences.Contains(reference.Use);
            FmImportMapping mapping = plan.GetReferenceMapping(reference.Use);
            return new FmImportEffectiveReference(reference.Use, reference.Target,
                mapping, active,
                active ? null : "Owner node or object is not included in the import.");
        }).ToArray();

        return new FmImportEffectiveGraph(
            Array.AsReadOnly(objectsResult), Array.AsReadOnly(nodesResult),
            Array.AsReadOnly(connectionsResult), Array.AsReadOnly(referencesResult));
    }
}
