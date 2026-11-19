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

/// <summary>A fully built but *unpublished* candidate object. This is not a
/// commit authorization; the object and provisional ID must be reconsidered
/// at the actual atomic commit boundary.</summary>
public sealed record FmImportPreparedObject(
    FmImportObjectKey Source, ObjectId ProvisionalId, SongObject Object);

public sealed record FmImportPreparationIssue(string Message,
    FmImportObjectKey? Source = null);

/// <summary>
/// The preparation result owns an independent set of off-document objects.
/// On any failure it exposes NO candidate objects or ID assignments.
/// Neither prepared objects nor the ID cursor reserve destination IDs.
/// </summary>
public sealed class FmImportPreparationResult
{
    internal FmImportPreparationResult(FmImportStageBResult preflight,
        IReadOnlyList<FmImportPreparationIssue> issues,
        IReadOnlyList<FmImportPreparedObject> objects,
        IReadOnlyDictionary<FmImportObjectKey, ObjectId> ids,
        ObjectId expectedNextId,
        IReadOnlyList<ObjectId> selectedRoots)
    {
        Preflight = preflight;
        Issues = issues;
        PreparedObjects = issues.Count == 0 && preflight.IsPreflightValid
            ? objects : [];
        ProvisionalIds = issues.Count == 0 && preflight.IsPreflightValid
            ? ids : new Dictionary<FmImportObjectKey, ObjectId>();
        ExpectedNextId = expectedNextId;
        SelectedRootIds = issues.Count == 0 && preflight.IsPreflightValid
            ? selectedRoots : [];
    }

    public FmImportStageBResult Preflight { get; }
    public IReadOnlyList<FmImportPreparationIssue> Issues { get; }
    public bool IsPrepared => Preflight.IsPreflightValid && Issues.Count == 0;
    public IReadOnlyList<FmImportPreparedObject> PreparedObjects { get; }
    public IReadOnlyDictionary<FmImportObjectKey, ObjectId> ProvisionalIds { get; }
    public ObjectId ExpectedNextId { get; }
    public IReadOnlyList<ObjectId> SelectedRootIds { get; }
}

/// <summary>
/// The first nonmutating half of the new transaction. Runs Stage B, computes
/// provisional IDs from the destination cursor, clones all effective copies
/// (including Sample data, Instrument tone uses and full FM graphs), and
/// resolves every distinct reference use. The real commit and undo unit are
/// intentionally NOT implemented here.
/// </summary>
public static class FmSynthImportTransactionPreparer
{
    public static FmImportPreparationResult Prepare(FmSynthImportPlan plan,
        IReadOnlyList<FmImportExactMatchSource> sources,
        SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);

        FmImportStageBResult checkedPlan =
            FmSynthImportStageBPreflight.Check(plan, sources, destination);
        ObjectId next = destination.NextUnallocatedObjectId;
        if (!checkedPlan.IsPreflightValid)
            return new(checkedPlan, [], [], new Dictionary<FmImportObjectKey,
                ObjectId>(), next, []);

        Dictionary<string, SongDocument> sourceSongs =
            sources.ToDictionary(s => s.Discovery.SourceDocumentIdentity,
                s => s.Source.Document, StringComparer.Ordinal);
        Dictionary<FmImportObjectKey, ObjectId> ids = [];
        List<FmImportPreparationIssue> errors = [];
        List<FmImportPreparedObject> copies = [];

        // Never call AllocateObjectId here. IDs cannot be guessed from the
        // highest used ObjectId because deleted IDs and prior reservations
        // still advance SongDocument's monotonically increasing cursor.
        FmImportEffectiveObject[] effectiveCopies = plan.Effective.Objects
            .Where(obj => obj.IsCopied).ToArray();
        if (next.IsNone || (ulong)next.Value + (ulong)effectiveCopies.Length - 1
            > uint.MaxValue)
        {
            errors.Add(new("The destination has insufficient ObjectIds to stage this import."));
            return Failed();
        }
        for (int i = 0; i < effectiveCopies.Length; i++)
            ids.Add(effectiveCopies[i].Key,
                new ObjectId((uint)((ulong)next.Value + (ulong)i)));

        if (ids.Values.Any(id => destination.Objects.ContainsKey(id)
            || destination.Tombstones.ContainsKey(id)))
        {
            errors.Add(new("The provisional ID range is already occupied; refresh the import plan."));
            return Failed();
        }

        Dictionary<FmImportObjectKey, SongObject> originals = [];
        try
        {
            foreach (FmImportEffectiveObject entry in effectiveCopies)
            {
                if (!sourceSongs.TryGetValue(entry.Key.SourceDocumentIdentity,
                        out SongDocument? song)
                    || !song.TryGet(entry.Key.SourceObjectId,
                        out SongObject? original) || original is null)
                    throw new InvalidOperationException(
                        $"Source object #{entry.Key.SourceObjectId.Value} disappeared.");
                originals.Add(entry.Key, original);
            }

            Dictionary<FmImportObjectKey, FmImportPreparedSample> sampleAssets =
                checkedPlan.PreparedSamples.ToDictionary(x => x.Source);
            Dictionary<FmImportObjectKey, FilterDefinition> newFilters = [];
            // Definitions must exist before FilterInstance uses are rebuilt.
            foreach ((FmImportObjectKey key, SongObject original) in originals)
                if (original is FilterDefinition filter)
                {
                    FilterDefinition cloned = new(ids[key], plan.GetProposedName(key).Name,
                        filter.Stages.Select(stage => new FilterStageDefinition(
                            stage.StageId, stage.Name, stage.Kind,
                            stage.DefaultParameters)).ToArray());
                    cloned.RestoreStageIdentity(filter.NextStageIdForPersistence,
                        filter.StageGenerationsForPersistence);
                    newFilters.Add(key, cloned);
                }

            ObjectId Resolve(FmImportReferenceKey use)
            {
                if (!plan.DiscoveredReferences.TryGetValue(use,
                        out FmImportObjectReference? reference))
                    throw new InvalidOperationException(
                        $"Missing imported use {use.Slot} for object #{use.Owner.SourceObjectId.Value}.");
                FmImportMapping mapping = plan.GetReferenceMapping(use);
                return mapping.Kind switch
                {
                    FmImportMappingKind.None => ObjectId.None,
                    FmImportMappingKind.ReuseExisting
                        when mapping.ExistingId is ObjectId local => local,
                    FmImportMappingKind.ImportCopy
                        when reference.Target is FmImportObjectKey target
                            && ids.TryGetValue(target, out ObjectId assigned)
                        => assigned,
                    _ => throw new InvalidOperationException(
                        $"Unable to resolve imported {use.Slot} use in object #{use.Owner.SourceObjectId.Value}."),
                };
            }

            FmImportReferenceKey Use(FmImportObjectKey owner,
                int? nodeId, int? tone, string slot)
                => new(owner, nodeId, tone, slot);

            FilterInstance? FilterUse(FmImportReferenceKey use,
                FilterInstance? authored)
            {
                ObjectId id = Resolve(use);
                if (id.IsNone)
                    return null;
                FilterDefinition definition;
                FmImportMapping mapping = plan.GetReferenceMapping(use);
                if (mapping.Kind == FmImportMappingKind.ImportCopy)
                {
                    FmImportObjectKey target = plan.DiscoveredReferences[use].Target
                        ?? throw new InvalidOperationException("Unassigned copied Filter.");
                    definition = newFilters[target];
                }
                else if (destination.TryGet(id, out SongObject? local)
                    && local is FilterDefinition localFilter)
                    definition = localFilter;
                else
                    throw new InvalidOperationException(
                        $"Filter #{id.Value} is not available for this use.");

                FilterInstance copy = new(definition);
                if (authored is null)
                    return copy;
                foreach (ConfiguredFilterStage stage in authored.GetStages())
                {
                    IReadOnlyDictionary<string, FilterParameterValue> overrides =
                        authored.GetOverrides(stage.StageId);
                    if (overrides.Count == 0)
                        continue;
                    if (!copy.GetStages().Any(candidate =>
                            candidate.StageId == stage.StageId
                            && candidate.Kind == stage.Kind)
                        || copy.GetStageGenerationForPersistence(stage.StageId)
                            != authored.GetStageGenerationForPersistence(stage.StageId))
                        throw new InvalidOperationException(
                            $"Filter stage #{stage.StageId} override cannot be preserved.");
                    copy.ReplaceStageOverrides(stage.StageId, overrides);
                }
                return copy;
            }

            ToneSpecification CopyTone(FmImportObjectKey owner,
                int toneIndex, ToneSpecification tone)
            {
                ObjectId? Optional(string slot)
                {
                    ObjectId id = Resolve(Use(owner, null, toneIndex, slot));
                    return id.IsNone ? null : id;
                }
                return new ToneSpecification
                {
                    SourceId = Resolve(Use(owner, null, toneIndex, "Source")),
                    PitchMultiplier = tone.PitchMultiplier,
                    Nna = tone.Nna,
                    VolumeEnvelopeId = Optional("VolumeEnvelope"),
                    PitchEnvelopeId = Optional("PitchEnvelope"),
                    PanningEnvelopeId = Optional("PanningEnvelope"),
                    FilterEnvelopeId = Optional("FilterEnvelope"),
                    FilterConfiguration = FilterUse(
                        Use(owner, null, toneIndex, "Filter"), tone.FilterConfiguration),
                };
            }

            FmSynthDefinition CopySynth(FmImportObjectKey owner,
                FmSynthDefinition source)
            {
                IReadOnlyDictionary<FmImportNodeKey, FmImportEffectiveNode> included =
                    plan.Effective.NodeByKey;
                HashSet<int> copiedNodeIds = included.Values.Where(x =>
                        x.Key.SourceSynth == owner && x.IsIncluded)
                    .Select(x => x.Key.NodeId).ToHashSet();
                bool Connected(int targetNode, int sourceNode, string port)
                    => plan.Effective.Connections.Any(edge =>
                        edge.IsActive && edge.Key.Consumer == new FmImportNodeKey(owner, targetNode)
                        && edge.Key.Source == new FmImportNodeKey(owner, sourceNode)
                        && string.Equals(edge.Key.PortIdentity, port,
                            StringComparison.OrdinalIgnoreCase));

                int? OptionalInput(int target, int? sourceId, string port)
                    => sourceId is int candidate && Connected(target, candidate, port)
                        ? candidate : null;

                FmSynthNode CloneNode(FmSynthNode node)
                    => node switch
                    {
                        FmConstantNode constant => new FmConstantNode(
                            constant.Id, constant.Value),
                        FmNoiseNode noise => new FmNoiseNode(
                            noise.Id, noise.Minimum, noise.Maximum),
                        FmOscillatorNode oscillator => new FmOscillatorNode(
                            oscillator.Id, oscillator.Waveform,
                            oscillator.FrequencyHz, oscillator.Minimum,
                            oscillator.Maximum,
                            OptionalInput(oscillator.Id, oscillator.MultiplierNodeId,
                                "frequency"),
                            oscillator.ExponentialMultiplier,
                            oscillator.Constant, oscillator.Curvature),
                        FmSoundSourceNode sound => new FmSoundSourceNode(
                            sound.Id,
                            Resolve(Use(owner, sound.Id, null, "Source")),
                            sound.FrequencyHz,
                            OptionalInput(sound.Id, sound.MultiplierNodeId,
                                "frequency"),
                            sound.ExponentialMultiplier, sound.Constant),
                        FmEnvelopeNode envelope => new FmEnvelopeNode(
                            envelope.Id,
                            Resolve(Use(owner, envelope.Id, null, "Envelope"))),
                        FmMultiNode multi => new FmMultiNode(
                            multi.Id,
                            OptionalInput(multi.Id, multi.InputNodeId, "audio"),
                            multi.Distribution, multi.Detune, multi.VoiceCount,
                            multi.HarmonicComponents, multi.ChordComponents,
                            multi.RandomizePhase, multi.GainCompensation,
                            multi.CycleLength),
                        FmOperatorNode operation => new FmOperatorNode(
                            operation.Id, operation.Operation,
                            operation.InputNodeIds.Where((sourceId, index) =>
                                Connected(operation.Id, sourceId,
                                    $"operand:{index}"))),
                        FmFilterNode filter => CopyFilterNode(filter),
                        _ => throw new NotSupportedException(
                            $"Unrecognized FM node {node.GetType().Name}"),
                    };

                FmFilterNode CopyFilterNode(FmFilterNode filter)
                {
                    FilterInstance? configuration = FilterUse(
                        Use(owner, filter.Id, null, "Filter"),
                        filter.Configuration);
                    List<FmFilterParameterConnection> connections = [];
                    foreach (FmFilterParameterConnection connection in filter.Connections)
                    {
                        string port = $"filter:{connection.StageId}:{(int)connection.Kind}"
                            + $":{connection.Generation}:{connection.ParameterName}";
                        if (Connected(filter.Id, connection.SourceNodeId, port))
                            connections.Add(connection);
                        else if (filter.StaleConnections.Contains(connection)
                            && copiedNodeIds.Contains(connection.SourceNodeId))
                            // Preserve inactive authored metadata for an
                            // unchanged stale port, not a removed node.
                            connections.Add(connection);
                    }
                    return new FmFilterNode(filter.Id,
                        OptionalInput(filter.Id, filter.AudioInputNodeId, "audio"),
                        configuration, connections, filter.TailSeconds);
                }

                FmSynthNode[] nodes = source.Graph.Nodes
                    .Where(node => copiedNodeIds.Contains(node.Id))
                    .Select(CloneNode).ToArray();
                FmSynthDefinition copy = new(ids[owner],
                    plan.GetProposedName(owner).Name,
                    new FmSynthGraph(nodes, source.Graph.OutputNodeId));
                copy.CopySourceColourFrom(source);
                copy.NodePositions.AddRange(source.NodePositions.Where(pos =>
                    copiedNodeIds.Contains(pos.NodeId)));
                copy.FilterBatchChangesNodeIds.UnionWith(
                    source.FilterBatchChangesNodeIds.Where(copiedNodeIds.Contains));
                foreach (FmSynthConnectionRoutingHint hint in
                    source.ConnectionRoutingHints)
                {
                    if (!copiedNodeIds.Contains(hint.SourceNodeId)
                        || !copiedNodeIds.Contains(hint.TargetNodeId))
                        continue;
                    FmImportEffectiveConnection? edge =
                        plan.Effective.Connections.FirstOrDefault(item =>
                            item.IsActive && item.Key.Source ==
                                new FmImportNodeKey(owner, hint.SourceNodeId)
                            && item.Key.Consumer ==
                                new FmImportNodeKey(owner, hint.TargetNodeId)
                            && item.Key.InputIndex == hint.TargetInputIndex);
                    if (edge is null || edge.DestinationPortIndex is not int ordinal)
                        continue;
                    copy.ConnectionRoutingHints.Add(
                        new FmSynthConnectionRoutingHint(hint.SourceNodeId,
                            hint.TargetNodeId, ordinal, hint.RoutePoints));
                }
                return copy;
            }

            // Materialize *every* object off-document. Dependencies may appear
            // later in the output list; all ID and Filter definition bindings
            // were resolved before building them.
            foreach (FmImportEffectiveObject entry in effectiveCopies)
            {
                FmImportObjectKey key = entry.Key;
                SongObject source = originals[key];
                SongObject clone = source switch
                {
                    FilterDefinition => newFilters[key],
                    EnvelopeDefinition envelope =>
                        FmSynthDocumentEditor.CloneEnvelope(envelope, ids[key]),
                    SampleDefinition sample when sampleAssets.TryGetValue(key,
                        out FmImportPreparedSample? encoded)
                        => SampleDefinition.CreateImportedCopy(ids[key], sample,
                            encoded.FileName, encoded.CopyEncodedBytes().Span),
                    InstrumentDefinition instrument => CopyInstrument(key, instrument),
                    ScriptInstrumentDefinition script => CopyScriptInstrument(key, script),
                    FmSynthDefinition synth => CopySynth(key, synth),
                    _ => throw new NotSupportedException(
                        $"Unsupported imported type {source.GetType().Name}."),
                };
                clone.Name = plan.GetProposedName(key).Name;
                copies.Add(new(key, ids[key], clone));
            }

            InstrumentDefinition CopyInstrument(FmImportObjectKey key,
                InstrumentDefinition source)
            {
                InstrumentDefinition output = new(ids[key],
                    plan.GetProposedName(key).Name)
                {
                    Divisions = source.Divisions,
                    Offset = source.Offset,
                    NnaOverride = source.NnaOverride,
                    NnaFadeDuration = source.NnaFadeDuration,
                };
                output.CopySourceColourFrom(source);
                for (int index = 0; index < source.ToneSpecifications.Count; index++)
                    output.ToneSpecifications.Add(
                        CopyTone(key, index, source.ToneSpecifications[index]));
                output.ToneTable.AddRange(source.ToneTable);
                return output;
            }

            ScriptInstrumentDefinition CopyScriptInstrument(FmImportObjectKey key,
                ScriptInstrumentDefinition source)
            {
                ScriptInstrumentDefinition output = new(ids[key],
                    plan.GetProposedName(key).Name)
                {
                    Source = source.Source,
                    PitchExpression = source.PitchExpression,
                    NnaExpression = source.NnaExpression,
                    NnaFadeDuration = source.NnaFadeDuration,
                };
                output.CopySourceColourFrom(source);
                for (int index = 0; index < source.ToneSpecifications.Count; index++)
                    output.ToneSpecifications.Add(
                        CopyTone(key, index, source.ToneSpecifications[index]));
                return output;
            }

            // A second check narrows, but does not eliminate, a concurrent
            // edit race. The later atomic commit MUST verify the cursor and
            // content fingerprints again on its own mutation boundary.
            if (destination.NextUnallocatedObjectId != next
                || !string.Equals(plan.DestinationDocumentFingerprint,
                    FmSynthImportAuthoringFingerprint.Capture(destination),
                    StringComparison.Ordinal)
                || sources.Any(item =>
                    !plan.SourceDocumentFingerprints.TryGetValue(
                        item.Discovery.SourceDocumentIdentity, out string? expected)
                    || !string.Equals(expected,
                        FmSynthImportAuthoringFingerprint.Capture(item.Source.Document),
                        StringComparison.Ordinal)))
                errors.Add(new("A song changed while the import was being prepared."));
        }
        catch (Exception error) when (error is ArgumentException
            or InvalidOperationException or KeyNotFoundException
            or NotSupportedException or OverflowException)
        {
            errors.Add(new($"Could not prepare the proposed import: {error.Message}"));
        }

        return errors.Count != 0 ? Failed() : new(
            checkedPlan,
            Array.Empty<FmImportPreparationIssue>(),
            Array.AsReadOnly(copies.ToArray()),
            new Dictionary<FmImportObjectKey, ObjectId>(ids),
            next,
            Array.AsReadOnly(plan.SelectedRoots.Select(root => ids[root]).ToArray()));

        FmImportPreparationResult Failed() => new(checkedPlan,
            Array.AsReadOnly(errors.ToArray()), [],
            new Dictionary<FmImportObjectKey, ObjectId>(), next, []);
    }
}
