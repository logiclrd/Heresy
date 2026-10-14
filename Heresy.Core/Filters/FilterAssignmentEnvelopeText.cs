using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;

namespace Heresy.Core.Filters;

/// <summary>An immutable name index captured from a particular song snapshot.
/// This never guesses an ID after a later rename or new same-named object.
/// Only Node and Script Envelopes are valid targets.</summary>
public sealed class FilterAssignmentEnvelopeNames
{
    public sealed record Entry(ObjectId Id, string Name);
    private readonly IReadOnlyList<Entry> _entries;

    private FilterAssignmentEnvelopeNames(Entry[] entries)
        => _entries = Array.AsReadOnly(entries);

    public static FilterAssignmentEnvelopeNames Capture(SongDocument song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return new FilterAssignmentEnvelopeNames(song.Objects.Values
            .OfType<EnvelopeDefinition>()
            .Where(e => e is NodeEnvelopeDefinition or ScriptEnvelopeDefinition)
            .OrderBy(e => e.Id.Value)
            .Select(e => new Entry(e.Id, e.Name))
            .ToArray());
    }

    public ObjectId Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Entry[] matches = _entries.Where(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new ArgumentException(
                $"Envelope '{name}' does not exist in this song snapshot."),
            _ => throw new ArgumentException(
                $"Envelope name '{name}' is ambiguous in this song snapshot."),
        };
    }
}

/// <summary>One nonstructural numeric Filter slot controlled by an
/// explicitly resolved same-song Envelope. Stage and parameter are canonical;
/// EnvelopeId is bound from the supplied immutable name snapshot, never from
/// a live name lookup during PCM playback.</summary>
public sealed record FilterAssignmentEnvelopeBinding(
    int StageId, string ParameterName, ObjectId EnvelopeId, int LineNumber);

/// <summary>Result of strict authoring validation. Numeric and Envelope
/// bindings remain separate so no fictional numeric value is assigned before
/// the playback control boundary. The authored text remains unchanged.</summary>
public sealed record FilterAssignmentEnvelopeAnalysis(
    IReadOnlyList<FilterStageAssignmentBatch> NumericBatches,
    IReadOnlyList<FilterAssignmentEnvelopeBinding> EnvelopeBindings)
{
    public int Count => NumericBatches.Sum(batch => batch.Assignments.Count)
        + EnvelopeBindings.Count;
}

/// <summary>
/// Parse stage.parameter = @"quoted Envelope name" alongside ordinary numeric
/// and choice assignments. Names are case-insensitive and may contain
/// spaces; within quotes, \\" and \\\\ escape a quote and backslash,
/// respectively. A malformed or ambiguous name is a strict authoring error.
/// For performance, playback should consume resolved IDs against its frozen
/// song, not call this live-authoring parser from an audio callback.
/// </summary>
public static class FilterAssignmentEnvelopeText
{
    public static FilterAssignmentEnvelopeAnalysis ParseAndValidate(
        FilterInstance selected, string text, FilterAssignmentEnvelopeNames names)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        string[] rows = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<FilterAssignmentEnvelopeBinding> bindings = [];
        string[] numericRows = new string[rows.Length];
        HashSet<(int StageId, string Parameter)> seen = [];

        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#'))
                continue;
            int equals = row.IndexOf('=');
            if (equals < 0)
                throw Invalid(i, "Expected stageName.parameterName = value.");
            string path = row[..equals].Trim();
            string rawValue = row[(equals + 1)..].Trim();
            if (!rawValue.StartsWith("@", StringComparison.Ordinal)
                && equals != row.LastIndexOf('='))
                throw Invalid(i, "Expected exactly one equals sign.");
            int dot = path.IndexOf('.');
            if (dot <= 0 || dot != path.LastIndexOf('.') ||
                dot == path.Length - 1 || rawValue.Length == 0)
                throw Invalid(i, "Expected one named stage and parameter, and a value.");

            string stageName = path[..dot].Trim();
            string parameterName = path[(dot + 1)..].Trim();
            FilterStageDefinition stage;
            try { stage = selected.Definition.GetStage(stageName); }
            catch (KeyNotFoundException)
            { throw Invalid(i, $"Unknown stage '{stageName}'."); }
            FilterParameterDescriptor descriptor =
                FilterCatalogue.GetParameters(stage.Kind).FirstOrDefault(d =>
                    string.Equals(d.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(i,
                    $"Unknown parameter '{parameterName}' on stage '{stageName}'.");
            if (!seen.Add((stage.StageId, descriptor.Name.ToUpperInvariant())))
                throw Invalid(i, $"Duplicate assignment for {stage.Name}.{descriptor.Name}.");

            if (!rawValue.StartsWith("@", StringComparison.Ordinal))
            {
                numericRows[i] = row;
                continue;
            }
            if (!rawValue.StartsWith("@\"", StringComparison.Ordinal))
                throw Invalid(i, "Envelope references must use @\"quoted name\".");
            if (!descriptor.IsModulatable)
                throw Invalid(i,
                    $"{stage.Name}.{descriptor.Name} cannot use an Envelope (structural or choice parameter).");

            string envelopeName = ReadQuotedName(rawValue, i);
            try
            {
                bindings.Add(new FilterAssignmentEnvelopeBinding(
                    stage.StageId, descriptor.Name, names.Resolve(envelopeName), i + 1));
            }
            catch (ArgumentException ex)
            {
                throw Invalid(i, ex.Message);
            }
        }

        // Keep the existing fully atomic numeric validation including coupled
        // FIR bounds, parameter normalization and original-stage preservation.
        // Envelope references do not insert artificial numeric values.
        IReadOnlyList<FilterStageAssignmentBatch> batches =
            FilterAssignmentText.ParseAndValidate(selected,
                string.Join("\n", numericRows));
        return new FilterAssignmentEnvelopeAnalysis(batches,
            Array.AsReadOnly(bindings.ToArray()));
    }

    /// <summary>
    /// Runtime salvage of individually valid Envelope references in their
    /// authored order. Invalid lines remain inert, as they are for numeric
    /// ApplyValidToClone. A later valid numeric assignment to the same
    /// parameter overrides an earlier Envelope reference; later references
    /// override earlier bindings. All lookups use the supplied frozen index.
    /// No editable song objects or script compilers are consulted here.
    /// </summary>
    public static IReadOnlyList<FilterAssignmentEnvelopeBinding> ResolveValidBindings(
        FilterInstance selected, string text, FilterAssignmentEnvelopeNames names)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<(int StageId, string Parameter),
            FilterAssignmentEnvelopeBinding> selectedBindings = [];
        string[] rows = text.Replace("\r\n", "\n")
            .Replace('\r', '\n').Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#'))
                continue;
            try
            {
                if (row.Contains("= @\"", StringComparison.Ordinal)
                    || row.Split('=', 2).Last().TrimStart()
                        .StartsWith("@", StringComparison.Ordinal))
                {
                    foreach (FilterAssignmentEnvelopeBinding binding
                        in ParseAndValidate(selected, row, names).EnvelopeBindings)
                    {
                        selectedBindings[(binding.StageId,
                            binding.ParameterName.ToUpperInvariant())] =
                            binding with { LineNumber = i + 1 };
                    }
                }
                else
                {
                    // A later valid literal wins over an earlier reference.
                    // Bad numeric lines cannot silently detach a valid one.
                    foreach (FilterStageAssignmentBatch batch in
                        FilterAssignmentText.ParseAndValidate(selected, row))
                        foreach (var item in batch.Assignments)
                            selectedBindings.Remove((batch.StageId,
                                item.Key.ToUpperInvariant()));
                }
            }
            catch (ArgumentException)
            {
                // Runtime's existing invalid-line salvage policy.
            }
        }
        return selectedBindings.Values.OrderBy(item => item.LineNumber).ToArray();
    }

    private static string ReadQuotedName(string literal, int lineIndex)
    {
        StringBuilder name = new();
        for (int i = 2; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c == '"')
            {
                if (i != literal.Length - 1)
                    throw Invalid(lineIndex,
                        "Unexpected text after the quoted Envelope name.");
                if (string.IsNullOrWhiteSpace(name.ToString()))
                    throw Invalid(lineIndex, "Envelope name cannot be empty.");
                return name.ToString();
            }
            if (c != '\\')
            {
                name.Append(c);
                continue;
            }
            if (++i >= literal.Length || literal[i] is not ('\\' or '"'))
                throw Invalid(lineIndex,
                    "Envelope name has an invalid escape; use backslash followed by a quote or backslash.");
            name.Append(literal[i]);
        }
        throw Invalid(lineIndex, "Unterminated quoted Envelope name.");
    }

    private static ArgumentException Invalid(int lineIndex, string detail)
        => new($"Line {lineIndex + 1}: {detail}", "text");
}
