using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Imports the complete, reachable object graph of selected ISound-backed
/// Synths. Source IDs are never carried verbatim into the destination song:
/// every Sample, Instrument, nested Synth, Envelope and Filter is rebound
/// through one shared old-to-new map, including dependencies of dependencies.
/// Encoded Samples are copied into pending assets owned by the destination
/// song; importing from a .hm archive does not retain paths into that archive.
/// </summary>
internal static class FmSynthSourceImport
{
    public static IReadOnlyList<FmSynthDefinition> ImportFromSong(
        DocumentWorkspace workspace,
        SongFmSynthImportSource source,
        IReadOnlyList<FmSynthDefinition> selected)
    {
        SongDocument original = source.Document;
        Dictionary<ObjectId, SongObject> reached = [];
        HashSet<ObjectId> active = [];
        Dictionary<ObjectId, EnvelopeDefinition> envelopes = [];
        Dictionary<ObjectId, FilterDefinition> filters = [];
        Dictionary<ObjectId, (string FileName, byte[] Bytes)> assets = [];

        void IncludeEnvelope(ObjectId? optionalId)
        {
            if (!optionalId.HasValue || optionalId.Value.IsNone)
                return;
            ObjectId id = optionalId.Value;
            if (!original.TryGet(id, out SongObject? obj)
                || obj is not EnvelopeDefinition envelope)
                throw new InvalidOperationException(
                    $"Synth import requires envelope object {id.Value}.");
            if (envelope is not (NodeEnvelopeDefinition or ScriptEnvelopeDefinition))
                throw new NotSupportedException(
                    $"Synth import cannot clone envelope {id.Value} of type {envelope.GetType().Name}.");
            envelopes.TryAdd(id, envelope);
        }

        void IncludeFilter(FilterInstance? instance)
        {
            if (instance is null)
                return;
            ObjectId id = instance.DefinitionId;
            if (!original.TryGet(id, out SongObject? obj)
                || obj is not FilterDefinition definition
                || !ReferenceEquals(definition, instance.Definition))
                throw new InvalidOperationException(
                    $"Synth import requires live Filter definition {id.Value}.");
            filters.TryAdd(id, definition);
        }

        void IncludeTone(ToneSpecification tone)
        {
            IncludeEnvelope(tone.VolumeEnvelopeId);
            IncludeEnvelope(tone.PitchEnvelopeId);
            IncludeEnvelope(tone.PanningEnvelopeId);
            IncludeEnvelope(tone.FilterEnvelopeId);
            IncludeFilter(tone.FilterConfiguration);
            Visit(tone.SourceId);
        }

        void Visit(ObjectId id)
        {
            if (id.IsNone || reached.ContainsKey(id))
                return;
            if (!active.Add(id))
                throw new InvalidOperationException(
                    $"Synth import source dependency cycle includes object #{id.Value}.");
            if (!original.TryGet(id, out SongObject? obj) || obj is null)
                throw new InvalidOperationException(
                    $"Synth import requires source object #{id.Value}, which is missing.");
            switch (obj)
            {
                case FmSynthDefinition synth:
                    foreach (FmSynthNode node in synth.Graph.Nodes)
                    {
                        switch (node)
                        {
                            case FmEnvelopeNode envelopeNode:
                                IncludeEnvelope(envelopeNode.EnvelopeId);
                                break;
                            case FmFilterNode filterNode:
                                IncludeFilter(filterNode.Configuration);
                                break;
                            case FmSoundSourceNode sourceNode:
                                Visit(sourceNode.SourceId);
                                break;
                            case FmConstantNode or FmNoiseNode or FmOscillatorNode or FmOperatorNode:
                                break;
                            default:
                                throw new NotSupportedException(
                                    $"Synth import cannot clone node type {node.GetType().Name}.");
                        }
                    }
                    break;
                case InstrumentDefinition instrument:
                    foreach (ToneSpecification tone in instrument.ToneSpecifications)
                        IncludeTone(tone);
                    break;
                case ScriptInstrumentDefinition instrument:
                    foreach (ToneSpecification tone in instrument.ToneSpecifications)
                        IncludeTone(tone);
                    break;
                case SampleDefinition sample:
                    if (sample.PcmData is null)
                        throw new InvalidOperationException(
                            $"Synth import cannot copy Sample #{id.Value} without decoded PCM.");
                    if (sample.PendingAsset is PendingSampleAsset pending)
                        assets.Add(id, (pending.FileName, pending.Bytes.ToArray()));
                    else if (sample.Asset is ExternalAssetReference asset)
                    {
                        using Stream stream = ExternalAssetIntegrity.OpenRead(asset.FullPath);
                        using MemoryStream copy = new();
                        stream.CopyTo(copy);
                        assets.Add(id, (Path.GetFileName(asset.FullPath), copy.ToArray()));
                    }
                    else
                        throw new InvalidOperationException(
                            $"Synth import Sample #{id.Value} has no encoded asset.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Object #{id.Value} cannot be used as an invocable Synth source.");
            }
            active.Remove(id);
            reached.Add(id, obj);
        }

        // Preflight the complete dependency graph and read all assets BEFORE
        // touching destination objects. Broken references and cycles must not
        // produce an invalid or partially imported song.
        foreach (FmSynthDefinition root in selected)
        {
            if (!original.TryGet(root.Id, out SongObject? registered)
                || !ReferenceEquals(root, registered))
                throw new InvalidOperationException(
                    $"Synth import selection #{root.Id.Value} is not in the source song.");
            Visit(root.Id);
        }

        IReadOnlyDictionary<ObjectId, ObjectId> filterMap =
            FilterDocumentImport.ImportFromSong(workspace,
                new SongFilterImportSource(source.Path, original,
                    original.Objects.Values.OfType<FilterDefinition>().ToArray()),
                filters.Keys);

        Dictionary<ObjectId, ObjectId> map = [];
        foreach (ObjectId id in envelopes.Keys.OrderBy(id => id.Value))
            map.Add(id, workspace.Document.AllocateObjectId());
        foreach (ObjectId id in reached.Keys.OrderBy(id => id.Value))
            map.Add(id, workspace.Document.AllocateObjectId());

        // Definitions already exist before tone Filter instances are decoded,
        // but no imported source references an original song identity.
        foreach (ObjectId id in envelopes.Keys.OrderBy(id => id.Value))
            workspace.Document.Add(FmSynthDocumentEditor.CloneEnvelope(envelopes[id], map[id]));

        ToneSpecification CloneTone(ToneSpecification tone)
        {
            ObjectId? RemapEnvelope(ObjectId? id) =>
                id.HasValue && !id.Value.IsNone ? map[id.Value] : id;
            return new ToneSpecification
            {
                SourceId = tone.SourceId.IsNone ? ObjectId.None : map[tone.SourceId],
                PitchMultiplier = tone.PitchMultiplier,
                Nna = tone.Nna,
                VolumeEnvelopeId = RemapEnvelope(tone.VolumeEnvelopeId),
                PitchEnvelopeId = RemapEnvelope(tone.PitchEnvelopeId),
                PanningEnvelopeId = RemapEnvelope(tone.PanningEnvelopeId),
                FilterEnvelopeId = RemapEnvelope(tone.FilterEnvelopeId),
                FilterConfiguration = tone.FilterConfiguration is FilterInstance filter
                    ? SongDocumentJson.DeserializeFilterInstance(
                        SongDocumentJson.SerializeFilterInstance(filter),
                        workspace.Document, filterMap)
                    : null,
            };
        }

        foreach (ObjectId id in reached.Keys.OrderBy(id => id.Value))
            switch (reached[id])
            {
                case SampleDefinition sample:
                    (string name, byte[] bytes) = assets[id];
                    workspace.Document.Add(SampleDefinition.CreateImportedCopy(
                        map[id], sample, name, bytes));
                    break;
                case InstrumentDefinition instrument:
                    InstrumentDefinition clone = new(map[id], instrument.Name)
                    {
                        Divisions = instrument.Divisions,
                        Offset = instrument.Offset,
                        NnaOverride = instrument.NnaOverride,
                        NnaFadeDuration = instrument.NnaFadeDuration,
                    };
                    clone.ToneSpecifications.AddRange(
                        instrument.ToneSpecifications.Select(CloneTone));
                    clone.ToneTable.AddRange(instrument.ToneTable);
                    workspace.Document.Add(clone);
                    break;
                case ScriptInstrumentDefinition instrument:
                    ScriptInstrumentDefinition scriptedCopy = new(map[id], instrument.Name)
                    {
                        Source = instrument.Source,
                        PitchExpression = instrument.PitchExpression,
                        NnaExpression = instrument.NnaExpression,
                        NnaFadeDuration = instrument.NnaFadeDuration,
                    };
                    scriptedCopy.ToneSpecifications.AddRange(
                        instrument.ToneSpecifications.Select(CloneTone));
                    workspace.Document.Add(scriptedCopy);
                    break;
            }

        foreach (ObjectId id in reached.Keys.OrderBy(id => id.Value))
            if (reached[id] is FmSynthDefinition synth)
                workspace.Document.Add(FmSynthDocumentEditor.CloneSynth(synth,
                    map[id], map, filterMap, workspace.Document, map));

        return selected.Select(synth =>
            (FmSynthDefinition)workspace.Document.Objects[map[synth.Id]]).ToArray();
    }
}
