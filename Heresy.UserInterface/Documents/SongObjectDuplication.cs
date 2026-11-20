using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Sequences;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A detached, non-mutating proposal. The candidate owns a new object identity
/// and independent authoring collections, but deliberately preserves same-song
/// references to existing objects. It is not yet a tree-command/UI operation.
/// </summary>
public sealed class SongObjectDuplicateDraft
{
    internal SongObjectDuplicateDraft(
        SongDocument document, ObjectId sourceId, ObjectId expectedId,
        SongObject candidate, string authoringSnapshot)
    {
        Document = document;
        SourceId = sourceId;
        ExpectedId = expectedId;
        Candidate = candidate;
        AuthoringSnapshot = authoringSnapshot;
    }

    internal SongDocument Document { get; }
    internal string AuthoringSnapshot { get; }
    public ObjectId SourceId { get; }
    public ObjectId ExpectedId { get; }
    public SongObject Candidate { get; }
}

/// <summary>
/// Shallow duplication of same-song definitions, preserving original external
/// object references while deeply copying each definition's *owned* state.
/// Graph-aware recursive duplication is a separate planned operation.
/// </summary>
public static class SongObjectDuplication
{
    public static SongObjectDuplicateDraft Prepare(
        SongDocument document, ObjectId sourceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.TryGet(sourceId, out SongObject? source)
            || source is null)
            throw new InvalidOperationException(
                $"Cannot duplicate missing song object #{sourceId.Value}.");

        ObjectId next = document.NextUnallocatedObjectId;
        if (next.IsNone)
            throw new InvalidOperationException("Object IDs are exhausted.");
        string expectedSnapshot =
            SongDocumentJson.SerializeAuthoringSnapshot(document);
        string name = SuggestName(document, source);
        SongObject clone = CloneWithReferences(source, next, name);
        return new(document, sourceId, next, clone, expectedSnapshot);
    }

    /// <summary>
    /// An accepted draft publishes as one atomic object edit, with a guarded
    /// Undo/Redo receipt. The tree placement currently defaults to the
    /// appropriate section root; sibling-folder placement and UI history
    /// wiring are separate follow-up slices.
    /// </summary>
    public static SongImportBatchEdit Commit(SongObjectDuplicateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        // A persisted source Sample is an external mutable dependency. Check
        // it *again* at the commit boundary: draft preparation cannot reserve
        // or freeze files written by unrelated processes.
        if (draft.Candidate is SampleDefinition candidate)
            VerifySampleCandidate(draft.Document, draft.SourceId, candidate);
        return draft.Document.CommitImportedObjects(
            [draft.Candidate], draft.ExpectedId,
            document =>
                document.TryGet(draft.SourceId, out SongObject? source)
                && source is not null
                && string.Equals(
                    SongDocumentJson.SerializeAuthoringSnapshot(document),
                    draft.AuthoringSnapshot, StringComparison.Ordinal));
    }

    /// <summary>Use the next free terminal numeric suffix rather than
    /// appending " 2" to a name which already ends in " 3". Names are
    /// case-insensitive for suggestions, but never a uniqueness invariant.</summary>
    public static string SuggestName(SongDocument document, SongObject source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        HashSet<string> occupied = document.Objects.Values
            .Where(item => item.Kind == source.Kind)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return SuggestName(source.Name, occupied);
    }

    internal static string SuggestName(string name, ISet<string> occupied)
    {
        string original = name.Trim();
        if (original.Length == 0)
            throw new InvalidOperationException(
                "An unnamed song object cannot be duplicated.");

        string stem = original;
        BigInteger suffix = 2;
        int lastSpace = original.LastIndexOf(' ');
        if (lastSpace > 0 && lastSpace < original.Length - 1
            && BigInteger.TryParse(original.AsSpan(lastSpace + 1),
                NumberStyles.None, CultureInfo.InvariantCulture,
                out BigInteger parsed))
        {
            stem = original[..lastSpace];
            suffix = parsed + 1;
        }
        string candidate;
        do
        {
            candidate = $"{stem} {suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        } while (occupied.Contains(candidate));
        return candidate;
    }

    internal static SongObject CloneWithReferences(
        SongObject source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap = null,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters = null)
        => source switch
        {
            DataSequenceDefinition sequence => CloneSequence(sequence, id, name, remap),
            ScriptSequenceDefinition script => new ScriptSequenceDefinition(id, name)
            {
                Source = script.Source,
            },
            DataPatternDefinition pattern => ClonePattern(pattern, id, name, remap),
            ScriptPatternDefinition script => CopyPatternProperties(
                script, new ScriptPatternDefinition(id, name)
                {
                    Source = script.Source,
                }),
            NodeEnvelopeDefinition or ScriptEnvelopeDefinition
                => CloneEnvelope((EnvelopeDefinition)source, id, name),
            FilterDefinition filter => CloneFilter(filter, id, name),
            SampleDefinition sample => CloneSample(sample, id, name),
            InstrumentDefinition instrument => CloneInstrument(instrument, id, name, remap, clonedFilters),
            ScriptInstrumentDefinition scriptInstrument =>
                CloneScriptInstrument(scriptInstrument, id, name, remap, clonedFilters),
            FmSynthDefinition synth => CloneSynth(synth, id, name, remap, clonedFilters),
            _ => throw new NotSupportedException(
                $"Duplicate Object does not yet support {source.GetType().Name}."),
        };

    internal static ObjectId Remap(
        ObjectId id, IReadOnlyDictionary<ObjectId, ObjectId>? map)
        => map is not null && map.TryGetValue(id, out ObjectId replacement)
            ? replacement : id;

    private static ObjectId? RemapOptional(
        ObjectId? id, IReadOnlyDictionary<ObjectId, ObjectId>? map)
        => id is ObjectId value ? Remap(value, map) : null;

    private static DataSequenceDefinition CloneSequence(
        DataSequenceDefinition source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap)
    {
        DataSequenceDefinition copy = new(id, name);
        copy.Entries.AddRange(source.Entries.Select(
            item => new SequenceEntry(Remap(item.PatternId, remap), item.StartRow)));
        return copy;
    }

    private static T CopyPatternProperties<T>(PatternDefinition source, T copy)
        where T : PatternDefinition
    {
        copy.RowCount = source.RowCount;
        copy.ChannelCount = source.ChannelCount;
        copy.MinorHighlightRows = source.MinorHighlightRows;
        copy.MajorHighlightRows = source.MajorHighlightRows;
        copy.CopySourceColourFrom(source);
        return copy;
    }

    private static DataPatternDefinition ClonePattern(
        DataPatternDefinition source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap)
    {
        DataPatternDefinition copy = CopyPatternProperties(
            source, new DataPatternDefinition(id, name));
        foreach ((int row, int column, PatternCell cell)
            in source.Grid.EnumerateNonEmptyCells())
        {
            PatternCell copied = copy.Grid.GetOrCreateCell(row, column);
            copied.Note = cell.Note is StartPatternNote note
                ? new StartPatternNote(Remap(note.SourceId, remap),
                    note.PitchMultiplier, note.PlaybackSpeedMultiplier,
                    note.Mixdown, RemapOptional(note.EventFilterDefinitionId, remap))
                : cell.Note is null ? null : cell.Note with { };
            copied.SourceId = Remap(cell.SourceId, remap);
            copied.Volume = cell.Volume;
            copied.Effects.AddRange(cell.Effects.Select(effect => effect with { }));
        }
        return copy;
    }

    private static EnvelopeDefinition CloneEnvelope(
        EnvelopeDefinition source, ObjectId id, string name)
    {
        EnvelopeDefinition cloned = FmSynthDocumentEditor.CloneEnvelope(source, id);
        cloned.Name = name;
        return cloned;
    }


    /// <summary>Verify at acceptance that the detached encoded Sample is
    /// still byte-for-byte identical to a freshly validated source read.
    /// Called for each Sample in a shallow or deep duplication batch.</summary>
    internal static void VerifySampleCandidate(
        SongDocument document, ObjectId sourceId, SampleDefinition candidate)
    {
        if (!document.TryGet(sourceId, out SongObject? original)
            || original is not SampleDefinition source)
            throw new InvalidOperationException(
                $"The Sample #{sourceId.Value} being duplicated is unavailable.");
        (string _, byte[] encoded) = ReadVerifiedSampleEncoding(source);
        if (candidate.PendingAsset is not PendingSampleAsset prepared
            || !prepared.Bytes.Span.SequenceEqual(encoded))
            throw new InvalidOperationException(
                $"Sample #{sourceId.Value} changed after duplicate preparation.");
    }

    private static (string FileName, byte[] Encoded) ReadVerifiedSampleEncoding(
        SampleDefinition source)
    {
        if (source.PcmData is not SamplePcmData originalPcm)
            throw new InvalidOperationException(
                $"Sample '{source.Name}' has no decoded PCM.");

        byte[] encoded;
        string fileName;
        string? expectedEncodedHash;
        if (source.PendingAsset is PendingSampleAsset pending)
        {
            encoded = pending.Bytes.ToArray();
            fileName = pending.FileName;
            expectedEncodedHash = pending.Sha256;
        }
        else if (source.Asset is ExternalAssetReference asset)
        {
            // OpenRead also resolves synthetic .hm archive entry paths.
            using Stream stream = ExternalAssetIntegrity.OpenRead(asset.FullPath);
            using MemoryStream output = new();
            stream.CopyTo(output);
            encoded = output.ToArray();
            fileName = Path.GetFileName(asset.FullPath);
            expectedEncodedHash = asset.Sha256;
        }
        else
            throw new InvalidOperationException(
                $"Sample '{source.Name}' has no encoded asset to duplicate.");

        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException(
                $"Sample '{source.Name}' has no usable asset file name.");
        // For an unhashed legacy external reference, decoded PCM remains the
        // ultimate proof that the fresh bytes match the loaded Sample.
        if (!string.IsNullOrWhiteSpace(expectedEncodedHash)
            && !string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(encoded)),
                expectedEncodedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Sample '{source.Name}' encoded asset has changed since it was loaded.");
        SamplePcmData fresh = SampleAudioCodec.Decode(encoded, fileName);
        if (!fresh.HasSameAudio(originalPcm))
            throw new InvalidOperationException(
                $"Sample '{source.Name}' encoded PCM no longer matches its loaded audio.");
        return (fileName, encoded);
    }

    private static SampleDefinition CloneSample(
        SampleDefinition source, ObjectId id, string name)
    {
        (string fileName, byte[] encoded) = ReadVerifiedSampleEncoding(source);
        SampleDefinition result =
            SampleDefinition.CreateImportedCopy(id, source, fileName, encoded);
        result.Name = name;
        return result;
    }

    private static FilterInstance? CloneFilterUse(
        FilterInstance? source,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
    {
        if (source is null)
            return null;
        if (clonedFilters is null
            || !clonedFilters.TryGetValue(source.DefinitionId,
                out FilterDefinition? newDefinition))
            return source.Clone();
        FilterInstance clone = new(newDefinition);
        foreach (FilterStageDefinition stage in source.Definition.Stages)
            clone.ReplaceStageOverrides(stage.StageId,
                source.GetOverrides(stage.StageId));
        return clone;
    }

    private static ToneSpecification CloneTone(
        ToneSpecification tone,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
        => new()
        {
            SourceId = Remap(tone.SourceId, remap),
            PitchMultiplier = tone.PitchMultiplier,
            Nna = tone.Nna,
            VolumeEnvelopeId = RemapOptional(tone.VolumeEnvelopeId, remap),
            PitchEnvelopeId = RemapOptional(tone.PitchEnvelopeId, remap),
            PanningEnvelopeId = RemapOptional(tone.PanningEnvelopeId, remap),
            FilterEnvelopeId = RemapOptional(tone.FilterEnvelopeId, remap),
            // The mutable Filter use belongs to this tone; the Filter
            // definition itself is a same-song *reference* and stays shared.
            FilterConfiguration = CloneFilterUse(
                tone.FilterConfiguration, clonedFilters),
        };

    private static InstrumentDefinition CloneInstrument(
        InstrumentDefinition source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
    {
        InstrumentDefinition copy = new(id, name)
        {
            Divisions = source.Divisions,
            Offset = source.Offset,
            NnaOverride = source.NnaOverride,
            NnaFadeDuration = source.NnaFadeDuration,
        };
        copy.CopySourceColourFrom(source);
        copy.ToneSpecifications.AddRange(
            source.ToneSpecifications.Select(tone =>
                CloneTone(tone, remap, clonedFilters)));
        copy.ToneTable.AddRange(source.ToneTable);
        return copy;
    }

    private static ScriptInstrumentDefinition CloneScriptInstrument(
        ScriptInstrumentDefinition source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
    {
        ScriptInstrumentDefinition copy = new(id, name)
        {
            Source = source.Source,
            PitchExpression = source.PitchExpression,
            NnaExpression = source.NnaExpression,
            NnaFadeDuration = source.NnaFadeDuration,
        };
        copy.CopySourceColourFrom(source);
        copy.ToneSpecifications.AddRange(
            source.ToneSpecifications.Select(tone =>
                CloneTone(tone, remap, clonedFilters)));
        return copy;
    }

    private static FmSynthDefinition CloneSynth(
        FmSynthDefinition source, ObjectId id, string name,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
    {
        FmSynthNode[] nodes = source.Graph.Nodes
            .Select(node => CloneSynthNode(node, remap, clonedFilters)).ToArray();
        FmSynthDefinition copy = new(id, name,
            new FmSynthGraph(nodes, source.Graph.OutputNodeId));
        copy.CopySourceColourFrom(source);
        copy.NodePositions.AddRange(source.NodePositions);
        copy.FilterBatchChangesNodeIds.UnionWith(
            source.FilterBatchChangesNodeIds);
        foreach (FmSynthConnectionRoutingHint hint in source.ConnectionRoutingHints)
            copy.ConnectionRoutingHints.Add(
                new FmSynthConnectionRoutingHint(hint.SourceNodeId,
                    hint.TargetNodeId, hint.TargetInputIndex, hint.RoutePoints));
        return copy;
    }

    /// <summary>
    /// Copy every authored graph node, including disconnected and stale
    /// Filter-connection metadata. Keep external ObjectIds unchanged for
    /// shallow duplication. The Filter configuration is owned by the cloned
    /// node (FmFilterNode itself clones the FilterInstance).
    /// </summary>
    private static FmSynthNode CloneSynthNode(
        FmSynthNode node,
        IReadOnlyDictionary<ObjectId, ObjectId>? remap,
        IReadOnlyDictionary<ObjectId, FilterDefinition>? clonedFilters)
        => node switch
    {
        FmConstantNode constant => new FmConstantNode(constant.Id, constant.Value),
        FmNoiseNode noise => new FmNoiseNode(noise.Id,
            noise.Minimum, noise.Maximum),
        FmOscillatorNode oscillator => new FmOscillatorNode(
            oscillator.Id, oscillator.Waveform, oscillator.FrequencyHz,
            oscillator.Minimum, oscillator.Maximum, oscillator.MultiplierNodeId,
            oscillator.ExponentialMultiplier, oscillator.Constant,
            oscillator.Curvature),
        FmSoundSourceNode sound => new FmSoundSourceNode(
            sound.Id, Remap(sound.SourceId, remap), sound.FrequencyHz,
            sound.MultiplierNodeId, sound.ExponentialMultiplier,
            sound.Constant),
        FmEnvelopeNode envelope => new FmEnvelopeNode(
            envelope.Id, Remap(envelope.EnvelopeId, remap)),
        FmMultiNode multi => new FmMultiNode(
            multi.Id, multi.InputNodeId, multi.Distribution, multi.Detune,
            multi.VoiceCount, multi.HarmonicComponents, multi.ChordComponents,
            multi.RandomizePhase, multi.GainCompensation, multi.CycleLength),
        FmOperatorNode operation => new FmOperatorNode(
            operation.Id, operation.Operation, operation.InputNodeIds),
        FmFilterNode filter => new FmFilterNode(
            filter.Id, filter.AudioInputNodeId,
            CloneFilterUse(filter.Configuration, clonedFilters),
            filter.Connections.Select(wire => wire with { }), filter.TailSeconds),
        _ => throw new NotSupportedException(
            $"Cannot duplicate FM node {node.GetType().Name}."),
    };
    private static FilterDefinition CloneFilter(
        FilterDefinition source, ObjectId id, string name)
    {
        FilterDefinition cloned = new(id, name,
            source.Stages.Select(stage => new FilterStageDefinition(
                stage.StageId, stage.Name, stage.Kind, stage.DefaultParameters)));
        cloned.RestoreStageIdentity(
            source.NextStageIdForPersistence,
            source.StageGenerationsForPersistence);
        return cloned;
    }
}
