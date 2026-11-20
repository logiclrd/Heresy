using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Central coverage of every persisted script field that may contain Heresy's
/// semantically recognized _O(id) intrinsic. Deep traversal and materialization
/// use the same list of fields, so an edited script cannot be silently omitted
/// from one of the two phases.
/// </summary>
internal static class SongObjectScriptReferences
{
    public static bool IsScripted(SongObject source)
        => source is ScriptSequenceDefinition or ScriptPatternDefinition
            or ScriptEnvelopeDefinition or ScriptInstrumentDefinition;

    public static IEnumerable<(string Field, string Source)> Fields(
        SongObject source)
    {
        switch (source)
        {
            case ScriptSequenceDefinition sequence:
                yield return (nameof(sequence.Source), sequence.Source);
                break;
            case ScriptPatternDefinition pattern:
                yield return (nameof(pattern.Source), pattern.Source);
                break;
            case ScriptEnvelopeDefinition envelope:
                yield return (nameof(envelope.Source), envelope.Source);
                break;
            case ScriptInstrumentDefinition instrument:
                yield return (nameof(instrument.Source), instrument.Source);
                yield return (nameof(instrument.PitchExpression),
                    instrument.PitchExpression);
                yield return (nameof(instrument.NnaExpression),
                    instrument.NnaExpression);
                break;
        }
    }

    public static IReadOnlyList<ObjectId> Discover(SongObject source)
    {
        List<ObjectId> result = [];
        foreach ((string field, string script) in Fields(source))
        {
            ScriptReferenceAnalysis analysis = ScriptReferenceAnalyzer.Analyze(script);
            if (!analysis.IsReliable)
                throw new InvalidOperationException(
                    $"Cannot deep-duplicate {source.GetType().Name}"
                    + $" #{source.Id.Value}: {field} contains unreliable"
                    + " script object references: "
                    + string.Join("; ", analysis.Diagnostics
                        .Where(item => item.Severity == ScriptDiagnosticSeverity.Error)
                        .Select(item => item.Message)));
            if (analysis.HasDynamicReferences)
                throw new InvalidOperationException(
                    $"Cannot deep-duplicate {source.GetType().Name}"
                    + $" #{source.Id.Value}: {field} contains computed"
                    + " _O(expression) references whose target IDs cannot"
                    + " be determined statically. Use a nonzero integer"
                    + " literal for deep duplication.");
            foreach (ScriptObjectReference reference in analysis.References)
                result.Add(reference.Id);
        }
        return result;
    }

    public static void Rewrite(
        SongObject source, SongObject copy,
        IReadOnlyDictionary<ObjectId, ObjectId> ids)
    {
        switch (source)
        {
            case ScriptSequenceDefinition sequence
                when copy is ScriptSequenceDefinition target:
                target.Source = ScriptObjectReferenceRewriter.Rewrite(
                    sequence.Source, ids);
                break;
            case ScriptPatternDefinition pattern
                when copy is ScriptPatternDefinition target:
                target.Source = ScriptObjectReferenceRewriter.Rewrite(
                    pattern.Source, ids);
                break;
            case ScriptEnvelopeDefinition envelope
                when copy is ScriptEnvelopeDefinition target:
                target.Source = ScriptObjectReferenceRewriter.Rewrite(
                    envelope.Source, ids);
                break;
            case ScriptInstrumentDefinition instrument
                when copy is ScriptInstrumentDefinition target:
                target.Source = ScriptObjectReferenceRewriter.Rewrite(
                    instrument.Source, ids);
                target.PitchExpression = ScriptObjectReferenceRewriter.Rewrite(
                    instrument.PitchExpression, ids);
                target.NnaExpression = ScriptObjectReferenceRewriter.Rewrite(
                    instrument.NnaExpression, ids);
                break;
            default:
                if (IsScripted(source))
                    throw new InvalidOperationException(
                        "Cannot rewrite the requested scripted object copy.");
                break;
        }
    }
}
