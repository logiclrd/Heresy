using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>A source snapshot paired with the same loaded document used for
/// discovery. Object IDs are scoped by the discovery's document identity.</summary>
public sealed record FmImportExactMatchSource(
    SongFmSynthImportSource Source, FmImportDiscoveredGraph Discovery);

/// <summary>Evidence for default reuse only. This is not a manual remap
/// decision, and is never inferred from names or numeric ObjectId equality.</summary>
public sealed record FmImportExactMatch(
    FmImportObjectKey Source, ObjectId ExistingId);

/// <summary>
/// Conservatively proves authored identity for Node/Script Envelopes,
/// Filters and Sample dependencies. Sample checks use SHA-256 of decoded PCM
/// as a fast filter, then compare every floating-point sample bit-for-bit.
/// Authored loop and channel positions must also agree. Source colour and
/// display name are deliberately not sound-identity attributes.
///
/// This does not mutate either SongDocument, allocate IDs, or read assets.
/// Its recommendations are a captured default snapshot; the destination-wide
/// validator must recheck current objects before the eventual commit.
/// </summary>
public static class FmSynthImportExactMatcher
{
    public static IReadOnlyList<FmImportExactMatch> Find(
        IEnumerable<FmImportExactMatchSource> sources,
        SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);

        // Deterministic tie-break independent of dictionary insertion order.
        SongObject[] local = destination.Objects.Values
            .Where(obj => obj is EnvelopeDefinition or FilterDefinition
                or SampleDefinition)
            .OrderBy(obj => obj.Id.Value).ToArray();
        // Each PCM buffer is hashed at most once per discovery pass, even
        // when multiple sources or local Samples share the same audio.
        Dictionary<SamplePcmData, byte[]> pcmHashes = [];
        List<FmImportExactMatch> results = [];
        HashSet<FmImportObjectKey> seen = [];
        HashSet<string> documents = new(StringComparer.Ordinal);

        foreach (FmImportExactMatchSource input in sources)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Source);
            ArgumentNullException.ThrowIfNull(input.Discovery);
            string identity = Path.GetFullPath(input.Source.Path);
            if (!string.Equals(identity, input.Discovery.SourceDocumentIdentity,
                    StringComparison.Ordinal)
                || !documents.Add(identity))
                throw new ArgumentException(
                    "Each discovery must have exactly one matching source document.",
                    nameof(sources));

            HashSet<FmImportObjectKey> roots =
                input.Discovery.SelectedSynthRoots.ToHashSet();
            foreach (FmImportDiscoveredObject discovered in input.Discovery.Objects)
            {
                FmImportObjectKey key = discovered.Key;
                if (key.SourceDocumentIdentity != identity || !seen.Add(key))
                    throw new ArgumentException("Invalid or duplicate source object identity.",
                        nameof(sources));
                if (roots.Contains(key)
                    || !input.Source.Document.TryGet(key.SourceObjectId,
                        out SongObject? original) || original is null
                    || original.GetType().Name != discovered.RuntimeType
                    || original.Kind != discovered.Kind)
                    continue;

                // Missing/wrong-kind objects cannot be matched; the existing
                // discovery issues remain visible to later validation.
                if (original is not EnvelopeDefinition and not FilterDefinition
                    and not SampleDefinition)
                    continue;

                SongObject? found = local.FirstOrDefault(candidate =>
                    original is SampleDefinition sourceSample
                        && candidate is SampleDefinition localSample
                        ? EqualSamples(sourceSample, localSample, pcmHashes)
                        : StrictlyEqual(original, candidate));
                if (found is not null)
                    results.Add(new FmImportExactMatch(key, found.Id));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Convenience construction of the normal plan. The same captured
    /// recommendations are retained for ResetToDefaults; the plan's first
    /// effective recalculation already includes automatic local reuses.</summary>
    public static FmSynthImportPlan CreatePlan(
        IEnumerable<FmImportExactMatchSource> sources,
        SongDocument destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        FmImportExactMatchSource[] inputs = sources.ToArray();
        IReadOnlyList<FmImportExactMatch> matches = Find(inputs, destination);
        FmSynthImportPlan plan = new(
            inputs.Select(input => input.Discovery),
            destination.Objects.Values.Select(obj =>
                new FmImportExistingName(obj.Kind, obj.Name)),
            destination.Objects.Values.OfType<FilterDefinition>());
        plan.InitializeExactMatchDefaults(matches);
        plan.CaptureSourceDocumentRevisions(inputs);
        plan.CaptureAuthoringFingerprints(inputs, destination);
        plan.AttachStageAContext(FmImportStageAContext.Capture(inputs, destination));
        return plan;
    }

    /// <summary>Compare strictly persisted authoring semantics, not runtime
    /// DSP state. Stage-name equality is deliberately strict because textual
    /// parameter paths can reference it; an audible-only match is insufficient.</summary>
    public static bool StrictlyEqual(SongObject source, SongObject candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        return (source, candidate) switch
        {
            (NodeEnvelopeDefinition a, NodeEnvelopeDefinition b)
                => EqualNodes(a, b),
            (ScriptEnvelopeDefinition a, ScriptEnvelopeDefinition b)
                => string.Equals(a.Source, b.Source, StringComparison.Ordinal)
                   && a.ReleaseDuration == b.ReleaseDuration,
            (FilterDefinition a, FilterDefinition b)
                => EqualFilters(a, b),
            (SampleDefinition a, SampleDefinition b)
                => EqualSamples(a, b, new Dictionary<SamplePcmData, byte[]>()),
            _ => false,
        };
    }

    private static bool EqualNodes(NodeEnvelopeDefinition a,
        NodeEnvelopeDefinition b)
        => a.SustainStartIndex == b.SustainStartIndex
           && a.SustainEndIndex == b.SustainEndIndex
           && a.SustainLoopMode == b.SustainLoopMode
           && a.Vmin == b.Vmin && a.Vmax == b.Vmax
           && a.Nodes.SequenceEqual(b.Nodes);

    private static bool EqualFilters(FilterDefinition a, FilterDefinition b)
    {
        if (a.NextStageIdForPersistence != b.NextStageIdForPersistence
            || a.Stages.Count != b.Stages.Count)
            return false;

        IReadOnlyDictionary<int, long> aGenerations =
            a.StageGenerationsForPersistence;
        IReadOnlyDictionary<int, long> bGenerations =
            b.StageGenerationsForPersistence;
        if (aGenerations.Count != bGenerations.Count
            || aGenerations.Any(entry =>
                !bGenerations.TryGetValue(entry.Key, out long generation)
                || generation != entry.Value))
            return false;

        for (int index = 0; index < a.Stages.Count; index++)
        {
            FilterStageDefinition left = a.Stages[index];
            FilterStageDefinition right = b.Stages[index];
            if (left.StageId != right.StageId || left.Kind != right.Kind
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !SameParameters(left.DefaultParameters,
                    right.DefaultParameters))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A mismatched SHA-256 rejects without comparing individual PCM values.
    /// A matching SHA-256 is NOT itself accepted: always verify the actual
    /// audio bits, rate, channels, frames, loop and spatial channel positions.
    /// The comparison is independent of display names, numeric IDs and source
    /// UI tint. This also supports the same PCM under different encodings.
    /// </summary>
    private static bool EqualSamples(SampleDefinition source,
        SampleDefinition local, Dictionary<SamplePcmData, byte[]> hashCache)
    {
        if (source.PcmData is not SamplePcmData a
            || local.PcmData is not SamplePcmData b
            || a.SampleRate != b.SampleRate
            || a.ChannelCount != b.ChannelCount
            || a.FrameCount != b.FrameCount
            || source.Loop != local.Loop
            || !source.SourceChannelPositions.SequenceEqual(
                local.SourceChannelPositions))
            return false;

        if (!Hash(a).AsSpan().SequenceEqual(Hash(b)))
            return false;

        // Compare raw IEEE-754 bits rather than float ==: -0 vs +0 and
        // distinct NaN payloads are distinguishable authored sample data.
        for (long frame = 0; frame < a.FrameCount; frame++)
            for (int channel = 0; channel < a.ChannelCount; channel++)
                if (BitConverter.SingleToInt32Bits(a.GetSample(frame, channel))
                    != BitConverter.SingleToInt32Bits(b.GetSample(frame, channel)))
                    return false;
        return true;

        byte[] Hash(SamplePcmData pcm)
        {
            if (!hashCache.TryGetValue(pcm, out byte[]? digest))
                hashCache.Add(pcm, digest = Sha256(pcm));
            return digest;
        }
    }

    private static byte[] Sha256(SamplePcmData pcm)
    {
        // Incremental, bounded-memory hash over the interleaved PCM's exact
        // IEEE-754 representation, independent of codec and file container.
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        int used = 0;
        for (long frame = 0; frame < pcm.FrameCount; frame++)
            for (int channel = 0; channel < pcm.ChannelCount; channel++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    buffer.Slice(used, sizeof(int)),
                    BitConverter.SingleToInt32Bits(pcm.GetSample(frame, channel)));
                used += sizeof(int);
                if (used == buffer.Length)
                {
                    hash.AppendData(buffer);
                    used = 0;
                }
            }
        if (used != 0)
            hash.AppendData(buffer[..used]);
        return hash.GetHashAndReset();
    }

    private static bool SameParameters(
        IReadOnlyDictionary<string, FilterParameterValue> left,
        IReadOnlyDictionary<string, FilterParameterValue> right)
        => left.Count == right.Count
           && left.All(pair =>
               right.TryGetValue(pair.Key, out FilterParameterValue? value)
               && value == pair.Value);
}
