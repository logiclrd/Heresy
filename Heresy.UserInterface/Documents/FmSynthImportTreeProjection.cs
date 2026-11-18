using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// TreeDataGrid-facing identity for a presentation row. Object/node occurrences
/// already have stable plan identities; unassigned references and Instrument
/// tone headers need independent identities without inventing a target object.
/// </summary>
public readonly record struct FmImportTreeRowKey(
    FmImportOccurrenceKey? Occurrence,
    FmImportReferenceKey? UnboundReference,
    FmImportObjectKey? ToneOwner,
    int? ToneIndex)
{
    public static FmImportTreeRowKey For(FmImportOccurrenceKey occurrence)
        => new(occurrence, null, null, null);

    public static FmImportTreeRowKey ForReference(FmImportReferenceKey use)
        => new(null, use, null, null);

    public static FmImportTreeRowKey ForTone(FmImportObjectKey owner, int tone)
        => new(null, null, owner, tone);
}

public enum FmImportTreeRowKind
{
    Object,
    GraphNode,
    GraphInput,
    Reference,
    InstrumentTone,
}

[Flags]
public enum FmImportTreeEdit
{
    None = 0,
    RenameCopy = 1,
    MapDefinition = 2,
    OverrideUse = 4,
    ClearUseOverride = 8,
    DisconnectInput = 16,
    ReconnectInput = 32,
    ExcludeNode = 64,
    IncludeNode = 128,
    PreserveDisconnected = 256,
    NavigateToCanonical = 512,
}

/// <summary>
/// One row occurrence, not an imported entity. Children and labels are read-only
/// snapshots, rebuilt after edits; the plan owns every editable decision.
/// </summary>
public sealed record FmImportTreeRow(
    FmImportTreeRowKey Key,
    FmImportTreeRowKind Kind,
    string Label,
    string Action,
    string? Reason,
    bool IsActive,
    bool IsCanonical,
    bool IsAlias,
    bool IsAutomaticExactMatch,
    bool IsExpanded,
    FmImportTreeEdit AvailableEdits,
    FmImportOccurrenceKey? Canonical,
    FmImportObjectKey? Object,
    FmImportNodeKey? Node,
    FmImportReferenceKey? Reference,
    FmImportGraphInputKey? Input,
    IReadOnlyList<FmImportTreeRow> Children)
{
    public bool IsDimmed => !IsActive || IsAlias;
}

/// <summary>Counts refer to planned entities/uses, never duplicate alias rows.
/// The two lists support the expandable summary directly below TreeDataGrid.
/// Error and warning messages come only from Stage A.</summary>
public sealed record FmImportTreeSummary(
    int NewObjectCount,
    int AutomaticallyReusedObjectCount,
    int ManuallyReusedObjectCount,
    int ReusedUseCount,
    int ExplicitlyRemappedUseCount,
    int DisconnectedInputCount,
    int ExcludedNodeCount,
    int ErrorCount,
    int WarningCount,
    bool? CanImport,
    IReadOnlyList<string> NewObjects,
    IReadOnlyList<string> ReusedObjects,
    IReadOnlyList<FmImportStageAIssue> Issues);

/// <summary>
/// Framework-independent TreeDataGrid row/summary controller. It owns only
/// ephemeral presentation state (expanded, selected, focused); FmSynthImportPlan
/// remains the sole authority for mapping, naming and graph edits.
///
/// A future Avalonia TreeDataGrid should bind its hierarchical rows to Roots,
/// its action editors to the Apply* methods, and its footer to Summary. This
/// class deliberately does not introduce the separately licensed Avalonia 12
/// TreeDataGrid package or change the existing import command.
/// </summary>
public sealed class FmSynthImportTreeProjection
{
    private readonly FmSynthImportPlan _plan;
    private readonly HashSet<FmImportTreeRowKey> _expanded = [];
    private readonly Dictionary<FmImportTreeRowKey, FmImportTreeRow> _byKey = [];
    private FmImportTreeRowKey? _selected;
    private FmImportTreeRowKey? _focused;

    public FmSynthImportTreeProjection(FmSynthImportPlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Refresh();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<FmImportTreeRow> Roots { get; private set; } = [];
    public FmImportTreeSummary Summary { get; private set; } = null!;
    public FmImportTreeRowKey? Selected => _selected;
    public FmImportTreeRowKey? Focused => _focused;
    public IReadOnlyDictionary<FmImportTreeRowKey, FmImportTreeRow> Rows => _byKey;

    public void Select(FmImportTreeRowKey key)
    {
        RequireRow(key);
        _selected = key;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Focus(FmImportTreeRowKey key)
    {
        RequireRow(key);
        _focused = key;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetExpanded(FmImportTreeRowKey key, bool expanded)
    {
        RequireRow(key);
        if (expanded) _expanded.Add(key);
        else _expanded.Remove(key);
        Refresh();
    }

    /// <summary>The canonical destination has stable identity even when its
    /// active owner moves after a Filter port or shared use is disconnected.
    /// Expands all ancestors, preserving other expanded rows and focus.</summary>
    public FmImportTreeRowKey? NavigateToCanonical(FmImportTreeRowKey key)
    {
        FmImportTreeRow row = RequireRow(key);
        if (row.Canonical is not FmImportOccurrenceKey canonical)
            return null;
        FmImportTreeRowKey destination = FmImportTreeRowKey.For(canonical);
        if (!_byKey.ContainsKey(destination))
            return null; // Never navigate to a missing row.
        ExpandAncestors(destination);
        _selected = destination;
        _focused = destination;
        Refresh();
        return destination;
    }

    public void ApplyDefinition(FmImportObjectKey key, FmImportMappingKind kind,
        ObjectId? existingId = null)
    {
        _plan.SetDefinitionMapping(key, kind, existingId);
        Refresh();
    }

    public void ApplyUse(FmImportReferenceKey use, FmImportMappingKind kind,
        ObjectId? existingId = null)
    {
        _plan.SetReferenceOverride(use, kind, existingId);
        Refresh();
    }

    public void ClearUse(FmImportReferenceKey use)
    {
        _plan.ClearReferenceOverride(use);
        Refresh();
    }

    public void SetConnection(FmImportGraphInputKey input, bool connected)
    {
        _plan.SetGraphInputConnected(input, connected);
        Refresh();
    }

    public void SetNodeExcluded(FmImportNodeKey node, bool excluded)
    {
        _plan.SetNodeExcluded(node, excluded);
        Refresh();
    }

    public void PreserveNode(FmImportNodeKey node, bool preserve)
    {
        _plan.SetPreserveDisconnectedNode(node, preserve);
        Refresh();
    }

    public void SetName(FmImportObjectKey key, string name)
    {
        _plan.SetProposedName(key, name);
        Refresh();
    }

    public void AcceptName(FmImportObjectKey key)
    {
        _plan.AcceptProposedName(key);
        Refresh();
    }

    public void Reset()
    {
        _plan.ResetToDefaults();
        Refresh();
    }

    /// <summary>Safe to call after an external plan edit; rebuilds from
    /// effective state rather than cached presentation objects.</summary>
    public void Refresh()
    {
        _byKey.Clear();
        var objects = _plan.DiscoveredObjects;
        var connections = _plan.DiscoveredConnections.Values
            .GroupBy(x => x.Target).ToDictionary(g => g.Key, g => g.ToArray());
        var references = _plan.DiscoveredReferences.Values
            .GroupBy(x => x.Use.Owner).ToDictionary(g => g.Key, g => g.ToArray());
        var graphs = _plan.DiscoveredSynthGraphs
            .ToDictionary(g => g.Synth);
        var effective = _plan.Effective;

        FmImportOccurrenceState? Occurrence(FmImportOccurrenceKey key)
        {
            try
            {
                return _plan.GetOccurrences(key.Entity)
                    .FirstOrDefault(state => state.Key == key);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        string Name(FmImportObjectKey key) =>
            objects.TryGetValue(key, out FmImportDiscoveredObject? obj)
                ? $"{obj.Name} ({obj.RuntimeType} #{key.SourceObjectId.Value})"
                : $"Missing object #{key.SourceObjectId.Value}";

        string Action(FmImportMapping mapping, FmImportObjectKey? original)
        {
            if (mapping.Kind == FmImportMappingKind.None)
                return "None / disconnected";
            if (mapping.Kind == FmImportMappingKind.ReuseExisting)
                return mapping.Provenance ==
                    FmImportMappingProvenance.AutomaticExactMatch
                        ? $"Automatic exact match — reuse local #{mapping.ExistingId?.Value}"
                        : $"Map to local #{mapping.ExistingId?.Value}";
            return original is FmImportObjectKey key
                && objects.TryGetValue(key, out _)
                ? $"Import copy — {_plan.GetProposedName(key).Name}"
                : "Import copy";
        }

        // All row identities are derived from immutable source/use keys.
        // The visited path bounds a malformed/cyclic authored node graph
        // while retaining its repeated occurrence as a linked row.
        FmImportTreeRow? ObjectRow(FmImportObjectKey key,
            FmImportOccurrenceKey location, HashSet<FmImportObjectKey> ancestry)
        {
            if (!objects.TryGetValue(key, out FmImportDiscoveredObject? obj))
                return null;
            FmImportOccurrenceState? state = Occurrence(location);
            bool canonical = state?.IsCanonical == true;
            bool active = state?.IsActive == true;
            FmImportMapping definition = _plan.GetDefinitionMapping(key);
            FmImportMapping mapping = location.Reference is FmImportReferenceKey use
                ? _plan.GetReferenceMapping(use) : definition;
            bool copied = effective.ObjectByKey[key].IsCopied;
            bool isRoot = location.Kind == FmImportOccurrenceKind.ObjectRoot;
            bool alias = !canonical;
            bool isAuto = canonical &&
                mapping.Kind == FmImportMappingKind.ReuseExisting &&
                mapping.Provenance == FmImportMappingProvenance.AutomaticExactMatch;
            FmImportTreeEdit edits = FmImportTreeEdit.None;
            if (canonical && !isRoot)
                edits |= FmImportTreeEdit.MapDefinition;
            if (canonical && copied)
                edits |= FmImportTreeEdit.RenameCopy;
            if (location.Reference is FmImportReferenceKey refer)
            {
                edits |= FmImportTreeEdit.OverrideUse;
                if (_plan.HasReferenceOverride(refer))
                    edits |= FmImportTreeEdit.ClearUseOverride;
            }
            if (alias)
                edits |= FmImportTreeEdit.NavigateToCanonical;

            string label = isRoot ? Name(key)
                : location.Reference is FmImportReferenceKey referenced
                  ? $"{referenced.Slot}: {Name(key)}" : Name(key);
            string? reason = !active ? "Use inactive or remapped." : null;
            List<FmImportTreeRow> children = [];
            // Expand the definition exactly once, at its canonical imported
            // occurrence. Links never recursively replicate graph contents.
            if (canonical && copied && ancestry.Add(key))
            {
                if (graphs.TryGetValue(key, out FmImportDiscoveredSynthGraph? graph))
                {
                    HashSet<FmImportNodeKey> roots = graph.PresentationRoots.ToHashSet();
                    foreach (FmImportNodeKey preserved in _plan.PreservedDisconnectedNodes)
                        if (preserved.SourceSynth == key) roots.Add(preserved);
                    foreach (FmImportNodeKey graphRoot in graph.PresentationRoots
                        .Concat(roots.Except(graph.PresentationRoots)
                            .OrderBy(x => x.NodeId)))
                        children.Add(NodeRow(graphRoot,
                            FmImportOccurrenceKey.NodeRoot(graphRoot),
                            ancestry, []));
                }
                else if (references.TryGetValue(key,
                    out FmImportObjectReference[]? objectUses))
                {
                    foreach (IGrouping<int?, FmImportObjectReference> group in
                        objectUses.GroupBy(x => x.Use.ToneIndex))
                    {
                        if (group.Key is int tone)
                        {
                            var toneKey = FmImportTreeRowKey.ForTone(key, tone);
                            var toneChildren = group.Select(x =>
                                ReferenceRow(x, ancestry)).ToArray();
                            children.Add(AddRow(new(toneKey,
                                FmImportTreeRowKind.InstrumentTone,
                                $"Tone {tone + 1}", "Tone references", null,
                                copied, true, false, false,
                                _expanded.Contains(toneKey), FmImportTreeEdit.None,
                                null, key, null, null, null,
                                Array.AsReadOnly(toneChildren))));
                        }
                        else
                        {
                            foreach (FmImportObjectReference toneReference in group)
                                children.Add(ReferenceRow(toneReference, ancestry));
                        }
                    }
                }
                ancestry.Remove(key);
            }
            return AddRow(new(FmImportTreeRowKey.For(location),
                FmImportTreeRowKind.Object, label, Action(mapping, key), reason,
                active, canonical, alias, isAuto,
                _expanded.Contains(FmImportTreeRowKey.For(location)),
                edits, state?.Canonical, key, null, location.Reference, null,
                Array.AsReadOnly(children.ToArray())));
        }

        FmImportTreeRow ReferenceRow(FmImportObjectReference reference,
            HashSet<FmImportObjectKey> ancestry)
        {
            FmImportReferenceKey use = reference.Use;
            FmImportMapping mapping = _plan.GetReferenceMapping(use);
            FmImportEffectiveReference live = effective.ReferenceByUse[use];
            if (reference.Target is FmImportObjectKey target
                && objects.ContainsKey(target))
            {
                FmImportTreeRow? objectRow = ObjectRow(target,
                    FmImportOccurrenceKey.ObjectUse(target, use), ancestry);
                if (objectRow is not null)
                    return objectRow;
            }
            // A missing/unassigned reference still needs a stable editable row.
            var key = FmImportTreeRowKey.ForReference(use);
            FmImportTreeEdit edits = FmImportTreeEdit.OverrideUse;
            if (_plan.HasReferenceOverride(use))
                edits |= FmImportTreeEdit.ClearUseOverride;
            return AddRow(new(key, FmImportTreeRowKind.Reference,
                $"{use.Slot}: (unassigned/missing)", Action(mapping, reference.Target),
                live.InactiveReason, live.IsActive, true, false, false,
                _expanded.Contains(key), edits, null, reference.Target, null, use,
                null, Array.Empty<FmImportTreeRow>()));
        }

        FmImportTreeRow NodeRow(FmImportNodeKey node,
            FmImportOccurrenceKey occurrence,
            HashSet<FmImportObjectKey> ancestry,
            HashSet<FmImportNodeKey> nodeAncestry)
        {
            FmImportOccurrenceState? state = Occurrence(occurrence);
            bool canonical = state?.IsCanonical == true;
            bool included = effective.NodeByKey.TryGetValue(node,
                out FmImportEffectiveNode? effectiveNode)
                && effectiveNode.IsIncluded;
            bool active = state?.IsActive == true;
            bool alias = !canonical;
            var key = FmImportTreeRowKey.For(occurrence);
            string nodeType = _plan.GetDiscoveredNodeType(node) ?? "Node";
            string name = $"#{node.NodeId} {nodeType.Replace("Fm", "")
                .Replace("Node", "")}";
            FmImportTreeEdit edits = FmImportTreeEdit.PreserveDisconnected;
            if (_plan.DiscoveredSynthGraphs.Any(graph =>
                graph.OutputNode == node))
                edits &= ~FmImportTreeEdit.PreserveDisconnected;
            else
            {
                edits |= _plan.IsNodeExcluded(node)
                    ? FmImportTreeEdit.IncludeNode : FmImportTreeEdit.ExcludeNode;
            }
            if (alias) edits |= FmImportTreeEdit.NavigateToCanonical;

            List<FmImportTreeRow> children = [];
            if (canonical && included && nodeAncestry.Add(node))
            {
                if (references.TryGetValue(node.SourceSynth,
                    out FmImportObjectReference[]? uses))
                    foreach (FmImportObjectReference reference in uses
                        .Where(r => r.Use.NodeId == node.NodeId))
                        children.Add(ReferenceRow(reference, ancestry));
                if (connections.TryGetValue(node,
                    out FmImportGraphConnection[]? inputs))
                    foreach (FmImportGraphConnection input in inputs
                        .OrderBy(c => c.TargetInputIndex)
                        .ThenBy(c => c.PortIdentity, StringComparer.Ordinal))
                    {
                        FmImportGraphInputKey inputKey = FmImportGraphInputKey.From(input);
                        FmImportEffectiveConnection stateEdge =
                            effective.ConnectionByKey[inputKey];
                        var inputRowKey = FmImportTreeRowKey.For(
                            FmImportOccurrenceKey.NodeUse(input.Source, inputKey));
                        FmImportTreeRow child = NodeRow(input.Source,
                            FmImportOccurrenceKey.NodeUse(input.Source, inputKey),
                            ancestry, nodeAncestry);
                        // Graph-input role is represented by the occurrence;
                        // label and editor metadata belong to this use.
                        FmImportTreeEdit inputEdits = child.AvailableEdits;
                        inputEdits &= ~(FmImportTreeEdit.ExcludeNode |
                            FmImportTreeEdit.IncludeNode |
                            FmImportTreeEdit.PreserveDisconnected);
                        if (input.IsActive)
                            inputEdits |= _plan.IsInputDisconnected(inputKey)
                                ? FmImportTreeEdit.ReconnectInput
                                : FmImportTreeEdit.DisconnectInput;
                        FmImportTreeRow role = child with
                        {
                            Kind = FmImportTreeRowKind.GraphInput,
                            Label = $"{input.PortLabel} → {child.Label}",
                            Action = stateEdge.IsActive
                                ? "Connected" : "Not imported",
                            Reason = stateEdge.InactiveReason,
                            IsActive = stateEdge.IsActive,
                            AvailableEdits = inputEdits,
                            Input = inputKey,
                        };
                        _byKey[inputRowKey] = role;
                        children.Add(role);
                    }
                nodeAncestry.Remove(node);
            }
            return AddRow(new(key, FmImportTreeRowKind.GraphNode,
                name, included ? "Include node" : "Not imported",
                !included ? "Node excluded or has no active use." : null,
                active, canonical, alias, false, _expanded.Contains(key),
                edits, state?.Canonical, null, node, null, occurrence.GraphInput,
                Array.AsReadOnly(children.ToArray())));
        }

        FmImportTreeRow AddRow(FmImportTreeRow row)
        {
            _byKey[row.Key] = row;
            return row;
        }

        List<FmImportTreeRow> roots = [];
        foreach (FmImportObjectKey selected in _plan.SelectedRoots)
        {
            FmImportTreeRow? root = ObjectRow(selected,
                FmImportOccurrenceKey.ObjectRoot(selected), []);
            if (root is not null) roots.Add(root);
        }
        Roots = Array.AsReadOnly(roots.ToArray());
        Summary = BuildSummary();

        if (_selected is FmImportTreeRowKey selectedKey && !_byKey.ContainsKey(selectedKey))
            _selected = null;
        if (_focused is FmImportTreeRowKey focusedKey && !_byKey.ContainsKey(focusedKey))
            _focused = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private FmImportTreeSummary BuildSummary()
    {
        FmImportEffectiveGraph graph = _plan.Effective;
        FmImportEffectiveObject[] copied = graph.Objects.Where(x => x.IsCopied).ToArray();
        FmImportEffectiveReference[] reused = graph.References.Where(x =>
            x.IsActive && x.Mapping.Kind == FmImportMappingKind.ReuseExisting).ToArray();
        FmImportEffectiveObject[] reusedDefinitions = graph.Objects.Where(x =>
            x.IsReferenced && !x.IsCopied
            && x.Mapping.Kind == FmImportMappingKind.ReuseExisting).ToArray();
        FmImportEffectiveReference[] remappedUses = graph.References.Where(x =>
            _plan.HasReferenceOverride(x.Use)
            && x.Mapping.Provenance == FmImportMappingProvenance.Manual).ToArray();
        FmImportEffectiveConnection[] disconnected = graph.Connections.Where(x =>
            !x.IsActive && _plan.DiscoveredConnections[x.Key].IsActive).ToArray();
        FmImportStageAIssue[] diagnostics = _plan.StageA?.Issues.ToArray() ?? [];
        string[] copiedNames = copied.Select(x => {
            FmImportDiscoveredObject source = _plan.DiscoveredObjects[x.Key];
            return $"{source.Kind}: {_plan.GetProposedName(x.Key).Name}";
        }).ToArray();
        string[] reuseNames = reusedDefinitions.Select(x => {
            FmImportDiscoveredObject source = _plan.DiscoveredObjects[x.Key];
            return $"{source.Kind}: {source.Name} → local #{x.Mapping.ExistingId?.Value}";
        }).ToArray();
        return new(copied.Length,
            reusedDefinitions.Count(x =>
                x.Mapping.Provenance == FmImportMappingProvenance.AutomaticExactMatch),
            reusedDefinitions.Count(x =>
                x.Mapping.Provenance == FmImportMappingProvenance.Manual),
            reused.Length,
            remappedUses.Length,
            disconnected.Length,
            graph.Nodes.Count(x => !x.IsIncluded),
            diagnostics.Count(x => x.Severity == FmImportStageAIssueSeverity.Error),
            diagnostics.Count(x => x.Severity == FmImportStageAIssueSeverity.Warning),
            _plan.StageA?.IsInternallyValid,
            Array.AsReadOnly(copiedNames), Array.AsReadOnly(reuseNames),
            Array.AsReadOnly(diagnostics));
    }

    private FmImportTreeRow RequireRow(FmImportTreeRowKey key)
        => _byKey.TryGetValue(key, out FmImportTreeRow? row) ? row :
            throw new ArgumentException("Unknown import projection row.", nameof(key));

    private void ExpandAncestors(FmImportTreeRowKey key)
    {
        bool Visit(FmImportTreeRow row, List<FmImportTreeRowKey> ancestors)
        {
            if (row.Key == key)
            {
                foreach (FmImportTreeRowKey ancestor in ancestors)
                    _expanded.Add(ancestor);
                return true;
            }
            ancestors.Add(row.Key);
            foreach (FmImportTreeRow child in row.Children)
                if (Visit(child, ancestors))
                    return true;
            ancestors.RemoveAt(ancestors.Count - 1);
            return false;
        }
        foreach (FmImportTreeRow root in Roots)
            if (Visit(root, []))
                return;
    }
}
