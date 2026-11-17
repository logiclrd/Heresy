using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>Definition-wide decisions and per-use overrides are separate.
/// These are proposals, not allocated destination objects.</summary>
public enum FmImportMappingKind
{
    ImportCopy,
    ReuseExisting,
    None,
}

public enum FmImportMappingProvenance
{
    Default,
    AutomaticExactMatch,
    Manual,
}

public sealed record FmImportMapping(
    FmImportMappingKind Kind,
    ObjectId? ExistingId,
    FmImportMappingProvenance Provenance)
{
    public static FmImportMapping DefaultCopy { get; } =
        new(FmImportMappingKind.ImportCopy, null, FmImportMappingProvenance.Default);

    public static FmImportMapping Unassigned { get; } =
        new(FmImportMappingKind.None, null, FmImportMappingProvenance.Default);
}

/// <summary>One semantic graph-input use, unaffected by presentation order
/// or the active/inactive flag on the discovered connection.</summary>
public readonly record struct FmImportGraphInputKey(
    FmImportNodeKey Source,
    FmImportNodeKey Consumer,
    int InputIndex,
    string PortIdentity)
{
    public static FmImportGraphInputKey From(FmImportGraphConnection connection)
        => new(connection.Source, connection.Target,
            connection.TargetInputIndex, connection.PortIdentity);
}

/// <summary>An imported definition or an authored node; never a row index.</summary>
public readonly record struct FmImportEntityKey(
    FmImportObjectKey? Object,
    FmImportNodeKey? Node)
{
    public static FmImportEntityKey ForObject(FmImportObjectKey key)
        => new(key, null);

    public static FmImportEntityKey ForNode(FmImportNodeKey key)
        => new(null, key);
}

public enum FmImportOccurrenceKind
{
    ObjectRoot,
    ObjectReference,
    NodeRoot,
    GraphInput,
}

/// <summary>The identity of a *presentation location*, separate from its
/// entity. An inactive alias can still resolve to its current canonical row.</summary>
public readonly record struct FmImportOccurrenceKey(
    FmImportEntityKey Entity,
    FmImportOccurrenceKind Kind,
    FmImportReferenceKey? Reference,
    FmImportGraphInputKey? GraphInput)
{
    public static FmImportOccurrenceKey ObjectRoot(FmImportObjectKey key)
        => new(FmImportEntityKey.ForObject(key),
            FmImportOccurrenceKind.ObjectRoot, null, null);

    public static FmImportOccurrenceKey ObjectUse(
        FmImportObjectKey key, FmImportReferenceKey use)
        => new(FmImportEntityKey.ForObject(key),
            FmImportOccurrenceKind.ObjectReference, use, null);

    public static FmImportOccurrenceKey NodeRoot(FmImportNodeKey key)
        => new(FmImportEntityKey.ForNode(key),
            FmImportOccurrenceKind.NodeRoot, null, null);

    public static FmImportOccurrenceKey NodeUse(
        FmImportNodeKey key, FmImportGraphInputKey use)
        => new(FmImportEntityKey.ForNode(key),
            FmImportOccurrenceKind.GraphInput, null, use);
}

public sealed record FmImportExistingName(SongObjectKind Kind, string Name);

public sealed record FmImportProposedName(string Name, bool IsPinned);

public sealed record FmImportOccurrenceState(
    FmImportOccurrenceKey Key, bool IsActive, bool IsCanonical,
    FmImportOccurrenceKey Canonical);

/// <summary>
/// Editable, destination-mutation-free decisions over one or more discovery
/// snapshots. This domain slice does NOT prove target compatibility, decide
/// graph liveness, allocate destination IDs, copy assets or commit an import.
/// Later effective-plan validation must resolve those concerns.
/// </summary>
public sealed class FmSynthImportPlan
{
    private readonly List<FmImportObjectKey> _objectOrder = [];
    private readonly List<FmImportObjectKey> _selectedRoots = [];
    private readonly HashSet<FmImportObjectKey> _rootSet = [];
    private readonly Dictionary<FmImportObjectKey, FmImportDiscoveredObject> _objects = [];
    private readonly Dictionary<FmImportNodeKey, FmImportDiscoveredSynthGraph> _nodes = [];
    private readonly Dictionary<FmImportReferenceKey, FmImportObjectReference> _references = [];
    private readonly Dictionary<FmImportGraphInputKey, FmImportGraphConnection> _connections = [];
    private readonly Dictionary<FmImportEntityKey, List<FmImportOccurrenceKey>> _occurrences = [];
    private readonly Dictionary<FmImportEntityKey, FmImportOccurrenceKey> _canonical = [];
    private readonly HashSet<FmImportNodeKey> _originalNodeRoots = [];
    private readonly HashSet<FmImportNodeKey> _outputNodes = [];
    private readonly HashSet<FmImportNodeKey> _preservedNodes = [];
    private readonly HashSet<FmImportNodeKey> _excludedNodes = [];
    private readonly HashSet<FmImportGraphInputKey> _disconnectedInputs = [];
    private readonly Dictionary<FmImportObjectKey, FmImportMapping> _definitionMappings = [];
    private readonly Dictionary<FmImportReferenceKey, FmImportMapping> _useOverrides = [];
    // Clearing a per-use override is still a manual action. An inherited
    // automatic match must never reacquire orange provenance until Reset.
    private readonly HashSet<FmImportReferenceKey> _manuallyTouchedUses = [];
    private readonly Dictionary<FmImportObjectKey, FmImportProposedName> _names = [];
    private readonly FmImportExistingName[] _existingNames;

    public IReadOnlyList<FmImportObjectKey> SelectedRoots => _selectedRoots.AsReadOnly();

    public FmSynthImportPlan(
        IEnumerable<FmImportDiscoveredGraph> sources,
        IEnumerable<FmImportExistingName>? existingNames = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _existingNames = existingNames?.ToArray() ?? [];
        HashSet<string> documentIdentities = new(StringComparer.Ordinal);
        foreach (FmImportDiscoveredGraph source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!documentIdentities.Add(source.SourceDocumentIdentity))
                throw new ArgumentException(
                    "A source document must be discovered only once per plan.", nameof(sources));

            foreach (FmImportDiscoveredObject obj in source.Objects)
            {
                if (obj.Key.SourceDocumentIdentity != source.SourceDocumentIdentity
                    || !_objects.TryAdd(obj.Key, obj))
                    throw new ArgumentException("Duplicate or mismatched import object key.", nameof(sources));
                _objectOrder.Add(obj.Key);
                AddOccurrence(FmImportOccurrenceKey.ObjectRoot(obj.Key));
            }
            foreach (FmImportObjectKey root in source.SelectedSynthRoots)
            {
                if (root.SourceDocumentIdentity != source.SourceDocumentIdentity
                    || !_rootSet.Add(root))
                    throw new ArgumentException("Duplicate or mismatched selected Synth.", nameof(sources));
                _selectedRoots.Add(root);
            }
            foreach (FmImportDiscoveredSynthGraph graph in source.SynthGraphs)
            {
                if (!_objects.ContainsKey(graph.Synth))
                    throw new ArgumentException("Synth graph has no discovered object.", nameof(sources));
                _outputNodes.Add(graph.OutputNode);
                foreach (FmImportNodeKey node in graph.Nodes)
                {
                    if (node.SourceSynth != graph.Synth || !_nodes.TryAdd(node, graph))
                        throw new ArgumentException("Duplicate or mismatched graph node.", nameof(sources));
                    AddOccurrence(FmImportOccurrenceKey.NodeRoot(node));
                }
                foreach (FmImportNodeKey root in graph.PresentationRoots)
                {
                    if (root != graph.OutputNode)
                        _originalNodeRoots.Add(root);
                }
            }
            foreach (FmImportObjectReference reference in source.ObjectReferences)
            {
                if (!_references.TryAdd(reference.Use, reference))
                    throw new ArgumentException("Duplicate import reference use.", nameof(sources));
                if (reference.Target is FmImportObjectKey target && _objects.ContainsKey(target))
                    AddOccurrence(FmImportOccurrenceKey.ObjectUse(target, reference.Use));
            }
            foreach (FmImportGraphConnection connection in source.Connections)
            {
                FmImportGraphInputKey key = FmImportGraphInputKey.From(connection);
                if (!_connections.TryAdd(key, connection))
                    throw new ArgumentException("Duplicate semantic graph input.", nameof(sources));
                // Stale Filter connections can refer to a node which no longer
                // exists. They must not create a phantom copied node.
                if (_nodes.ContainsKey(connection.Source))
                    AddOccurrence(FmImportOccurrenceKey.NodeUse(connection.Source, key));
            }
        }

        foreach (FmImportObjectKey root in _selectedRoots)
        {
            if (!_objects.TryGetValue(root, out FmImportDiscoveredObject? obj)
                || obj.Kind != SongObjectKind.FmSynth)
                throw new ArgumentException("Selected root is not a discovered Synth.", nameof(sources));
        }
        RefreshNames();
        RehomeCanonicalOwners();
    }

    public FmImportMapping GetDefinitionMapping(FmImportObjectKey key)
    {
        RequireObject(key);
        return _definitionMappings.GetValueOrDefault(key) ?? FmImportMapping.DefaultCopy;
    }

    public FmImportMapping GetReferenceMapping(FmImportReferenceKey use)
    {
        FmImportObjectReference reference = RequireReference(use);
        if (_useOverrides.TryGetValue(use, out FmImportMapping? overridden))
            return overridden;
        FmImportMapping inherited =
            reference.Target is FmImportObjectKey target && _objects.ContainsKey(target)
                ? GetDefinitionMapping(target) : FmImportMapping.Unassigned;
        return _manuallyTouchedUses.Contains(use)
            && inherited.Provenance == FmImportMappingProvenance.AutomaticExactMatch
            ? inherited with { Provenance = FmImportMappingProvenance.Manual }
            : inherited;
    }

    public bool HasReferenceOverride(FmImportReferenceKey use)
    {
        RequireReference(use);
        return _useOverrides.ContainsKey(use);
    }

    /// <summary>Manual choices always erase automatic provenance, even
    /// when choosing the exact same destination object again.</summary>
    public void SetDefinitionMapping(FmImportObjectKey key,
        FmImportMappingKind kind, ObjectId? existingId = null)
    {
        RequireObject(key);
        if (_rootSet.Contains(key) && kind != FmImportMappingKind.ImportCopy)
            throw new InvalidOperationException(
                "An explicitly selected Synth always requires a fresh copy.");
        _definitionMappings[key] = Manual(kind, existingId);
        RefreshNames();
        RehomeCanonicalOwners();
    }

    /// <summary>Called only after a later strict-equivalence engine has
    /// independently proved the candidate. Never changes a manual choice.</summary>
    public bool RecommendExactReuse(FmImportObjectKey key, ObjectId existingId)
    {
        RequireObject(key);
        if (_rootSet.Contains(key)
            || (_definitionMappings.TryGetValue(key, out FmImportMapping? current)
                && current.Provenance == FmImportMappingProvenance.Manual))
            return false;
        if (existingId.IsNone)
            throw new ArgumentException("An existing object must have an ID.", nameof(existingId));
        _definitionMappings[key] = new(FmImportMappingKind.ReuseExisting,
            existingId, FmImportMappingProvenance.AutomaticExactMatch);
        RefreshNames();
        RehomeCanonicalOwners();
        return true;
    }

    public void SetReferenceOverride(FmImportReferenceKey use,
        FmImportMappingKind kind, ObjectId? existingId = null)
    {
        RequireReference(use);
        _useOverrides[use] = Manual(kind, existingId);
        _manuallyTouchedUses.Add(use);
        RehomeCanonicalOwners();
    }

    public void ClearReferenceOverride(FmImportReferenceKey use)
    {
        RequireReference(use);
        _useOverrides.Remove(use);
        _manuallyTouchedUses.Add(use);
        RehomeCanonicalOwners();
    }

    /// <summary>Suppress an authored active input use. Inactive stale Filter
    /// metadata cannot be made into a live port by toggling its row.</summary>
    public void SetGraphInputConnected(FmImportGraphInputKey input, bool connected)
    {
        if (!_connections.TryGetValue(input, out FmImportGraphConnection? edge))
            throw new ArgumentException("Unknown graph input.", nameof(input));
        if (connected && !edge.IsActive)
            throw new InvalidOperationException("Stale Filter ports require semantic rewiring.");
        if (connected)
            _disconnectedInputs.Remove(input);
        else
            _disconnectedInputs.Add(input);
        RehomeCanonicalOwners();
    }

    public void SetNodeExcluded(FmImportNodeKey node, bool excluded)
    {
        RequireNode(node);
        if (excluded && _outputNodes.Contains(node))
            throw new InvalidOperationException("The designated output cannot be excluded.");
        if (excluded)
            _excludedNodes.Add(node);
        else
            _excludedNodes.Remove(node);
        RehomeCanonicalOwners();
    }

    public void SetPreserveDisconnectedNode(FmImportNodeKey node, bool preserve)
    {
        RequireNode(node);
        if (preserve)
            _preservedNodes.Add(node);
        else
            _preservedNodes.Remove(node);
        RehomeCanonicalOwners();
    }

    public FmImportProposedName GetProposedName(FmImportObjectKey key)
    {
        RequireObject(key);
        return _names[key];
    }

    public void SetProposedName(FmImportObjectKey key, string name)
    {
        RequireObject(key);
        ArgumentNullException.ThrowIfNull(name);
        // Deliberate duplicate names are legal; pin verbatim rather than
        // running user input through the collision-avoidance heuristic.
        _names[key] = new(name, true);
        RefreshNames();
    }

    public void AcceptProposedName(FmImportObjectKey key)
    {
        RequireObject(key);
        _names[key] = _names[key] with { IsPinned = true };
    }

    public FmImportOccurrenceKey GetCanonical(FmImportEntityKey entity)
        => _canonical.TryGetValue(entity, out FmImportOccurrenceKey owner)
            ? owner : throw new ArgumentException("Unknown import entity.", nameof(entity));

    public IReadOnlyList<FmImportOccurrenceState> GetOccurrences(FmImportEntityKey entity)
    {
        if (!_occurrences.TryGetValue(entity, out List<FmImportOccurrenceKey>? list))
            throw new ArgumentException("Unknown import entity.", nameof(entity));
        FmImportOccurrenceKey canonical = _canonical[entity];
        return list.Select(key => new FmImportOccurrenceState(
            key, IsActive(key), key == canonical, canonical)).ToArray();
    }

    public void ResetToDefaults()
    {
        _definitionMappings.Clear();
        _useOverrides.Clear();
        _manuallyTouchedUses.Clear();
        _disconnectedInputs.Clear();
        _excludedNodes.Clear();
        _preservedNodes.Clear();
        _names.Clear();
        _canonical.Clear();
        RefreshNames();
        RehomeCanonicalOwners();
    }

    private static FmImportMapping Manual(FmImportMappingKind kind, ObjectId? id)
    {
        if ((kind == FmImportMappingKind.ReuseExisting) != (id is ObjectId existing && !existing.IsNone))
            throw new ArgumentException(
                "Reuse requires an existing non-None ID; other decisions must not carry one.");
        return new(kind, id, FmImportMappingProvenance.Manual);
    }

    private void RequireObject(FmImportObjectKey key)
    {
        if (!_objects.ContainsKey(key))
            throw new ArgumentException("Unknown source object.", nameof(key));
    }

    private void RequireNode(FmImportNodeKey key)
    {
        if (!_nodes.ContainsKey(key))
            throw new ArgumentException("Unknown authored graph node.", nameof(key));
    }

    private FmImportObjectReference RequireReference(FmImportReferenceKey use)
        => _references.TryGetValue(use, out FmImportObjectReference? reference)
            ? reference : throw new ArgumentException("Unknown source object use.", nameof(use));

    private void AddOccurrence(FmImportOccurrenceKey key)
    {
        if (!_occurrences.TryGetValue(key.Entity, out List<FmImportOccurrenceKey>? list))
            _occurrences[key.Entity] = list = [];
        if (!list.Contains(key))
            list.Add(key);
    }

    private bool IsActive(FmImportOccurrenceKey key) => key.Kind switch
    {
        FmImportOccurrenceKind.ObjectRoot =>
            _rootSet.Contains(key.Entity.Object!.Value),
        FmImportOccurrenceKind.NodeRoot =>
            !_excludedNodes.Contains(key.Entity.Node!.Value)
            && (_outputNodes.Contains(key.Entity.Node.Value)
                || _originalNodeRoots.Contains(key.Entity.Node.Value)
                || _preservedNodes.Contains(key.Entity.Node.Value)),
        FmImportOccurrenceKind.ObjectReference =>
            ObjectReferenceActive(key),
        FmImportOccurrenceKind.GraphInput =>
            GraphInputActive(key),
        _ => false,
    };

    private bool ObjectReferenceActive(FmImportOccurrenceKey occurrence)
    {
        FmImportReferenceKey use = occurrence.Reference!.Value;
        FmImportObjectReference reference = _references[use];
        if (reference.Target != occurrence.Entity.Object)
            return false;
        if (use.NodeId is int id
            && _excludedNodes.Contains(new FmImportNodeKey(use.Owner, id)))
            return false;
        FmImportMapping choice = GetReferenceMapping(use);
        // Definition-level reuse still has a live use of that *definition*;
        // a per-use remap to another local target does not.
        return choice.Kind != FmImportMappingKind.None
            && (!_useOverrides.ContainsKey(use)
                || choice.Kind == FmImportMappingKind.ImportCopy);
    }

    private bool GraphInputActive(FmImportOccurrenceKey occurrence)
    {
        FmImportGraphInputKey input = occurrence.GraphInput!.Value;
        FmImportGraphConnection edge = _connections[input];
        return edge.IsActive && !_disconnectedInputs.Contains(input)
            && !_excludedNodes.Contains(input.Source)
            && !_excludedNodes.Contains(input.Consumer);
    }

    private void RehomeCanonicalOwners()
    {
        foreach ((FmImportEntityKey entity, List<FmImportOccurrenceKey> locations)
            in _occurrences)
        {
            if (_canonical.TryGetValue(entity, out FmImportOccurrenceKey current)
                && IsActive(current))
                continue; // Never steal ownership back when an old use returns.
            FmImportOccurrenceKey? replacement = locations
                .Where(IsActive).Cast<FmImportOccurrenceKey?>().FirstOrDefault();
            if (replacement is FmImportOccurrenceKey active)
                _canonical[entity] = active;
            else if (!_canonical.ContainsKey(entity))
                _canonical[entity] = locations[0];
            // With no active use, retain the stable inactive canonical
            // location for alias navigation until effective-plan pruning.
        }
    }

    private void RefreshNames()
    {
        Dictionary<SongObjectKind, HashSet<string>> occupied = [];
        HashSet<string> For(SongObjectKind kind)
        {
            if (!occupied.TryGetValue(kind, out HashSet<string>? set))
                occupied[kind] = set = new(StringComparer.OrdinalIgnoreCase);
            return set;
        }
        foreach (FmImportExistingName existing in _existingNames)
            For(existing.Kind).Add(existing.Name);
        foreach (FmImportObjectKey key in _objectOrder)
        {
            if (_names.TryGetValue(key, out FmImportProposedName? pinned) && pinned.IsPinned
                && GetDefinitionMapping(key).Kind == FmImportMappingKind.ImportCopy)
                For(_objects[key].Kind).Add(pinned.Name);
        }
        foreach (FmImportObjectKey key in _objectOrder)
        {
            FmImportDiscoveredObject obj = _objects[key];
            if (_names.TryGetValue(key, out FmImportProposedName? current) && current.IsPinned)
                continue;
            HashSet<string> names = For(obj.Kind);
            string suggestion = obj.Name;
            if (GetDefinitionMapping(key).Kind == FmImportMappingKind.ImportCopy)
            {
                for (int suffix = 2; names.Contains(suggestion); suffix++)
                    suggestion = obj.Name + " " + suffix;
                names.Add(suggestion);
            }
            _names[key] = new(suggestion, false);
        }
    }
}
