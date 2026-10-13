using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Heresy.Core.Filters;

/// <summary>One complete atomic text-command batch for a stable Filter stage.
/// Text paths use the authored stage name, while the command uses StageId so
/// renames and reordering do not accidentally retarget it.</summary>
public sealed record FilterStageAssignmentBatch(
    int StageId,
    IReadOnlyList<KeyValuePair<string, FilterParameterValue>> Assignments);

/// <summary>
/// Native effect's stageName.parameterName = value authoring language.
/// Validation is deliberately the same as FilterInstance's typed mutation
/// rules. Callers may never publish a partially parsed or partially validated
/// command; use ApplyToClone before publishing the resulting instance.
/// </summary>
public static class FilterAssignmentText
{
    public static IReadOnlyList<FilterStageAssignmentBatch> ParseAndValidate(
        FilterInstance selected, string text)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<int, List<KeyValuePair<string, FilterParameterValue>>> groups = [];
        List<int> order = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int equals = line.IndexOf('=');
            if (equals < 0 || equals != line.LastIndexOf('='))
                throw Invalid(i, "Expected stageName.parameterName = value.");
            string path = line[..equals].Trim();
            string raw = line[(equals + 1)..].Trim();
            int dot = path.IndexOf('.');
            if (dot <= 0 || dot != path.LastIndexOf('.')
                || dot == path.Length - 1 || raw.Length == 0)
                throw Invalid(i, "Expected one named stage and parameter, and a value.");
            string name = path[..dot].Trim();
            string parameter = path[(dot + 1)..].Trim();
            FilterStageDefinition stage;
            try { stage = selected.Definition.GetStage(name); }
            catch (KeyNotFoundException) { throw Invalid(i, $"Unknown stage '{name}'."); }
            FilterParameterDescriptor descriptor =
                FilterCatalogue.GetParameters(stage.Kind).FirstOrDefault(p =>
                    string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(i, $"Unknown parameter '{parameter}' on stage '{name}'.");
            string key = $"{stage.StageId}:{descriptor.Name}";
            if (!seen.Add(key))
                throw Invalid(i, $"Duplicate assignment for {stage.Name}.{descriptor.Name}.");
            FilterParameterValue value;
            if (descriptor.Kind == FilterParameterValueKind.Number)
            {
                if (!double.TryParse(raw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double number))
                    throw Invalid(i, $"'{raw}' is not an invariant-culture number.");
                value = FilterParameterValue.FromNumber(number);
            }
            else
                value = FilterParameterValue.FromChoice(raw);
            try { value = descriptor.Normalize(value); }
            catch (ArgumentException e)
            {
                throw Invalid(i, $"{stage.Name}.{descriptor.Name}: {e.Message}");
            }
            if (!groups.TryGetValue(stage.StageId,
                out List<KeyValuePair<string, FilterParameterValue>>? entries))
            {
                entries = [];
                groups.Add(stage.StageId, entries);
                order.Add(stage.StageId);
            }
            entries.Add(new(descriptor.Name, value));
        }

        // Interdependent constraints (e.g., FIR low/high band edges)
        // are validated against a copy of the *entire* existing use. A
        // failure cannot modify even an earlier valid stage.
        FilterInstance candidate = selected.Clone();
        try
        {
            foreach (int id in order)
                candidate.SetStageParameters(id, groups[id]);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"Invalid Filter assignment batch: {e.Message}",
                nameof(text), e);
        }
        return order.Select(id => new FilterStageAssignmentBatch(id,
            Array.AsReadOnly(groups[id].ToArray()))).ToArray();
    }

    public static FilterInstance ApplyToClone(
        FilterInstance selected, string text)
    {
        var batches = ParseAndValidate(selected, text);
        FilterInstance result = selected.Clone();
        foreach (var batch in batches)
            result.SetStageParameters(batch.StageId, batch.Assignments);
        return result;
    }

    /// <summary>
    /// Best-effort, read-only preview of authored parameters for one stage.
    /// A malformed or unrelated line cannot prevent other valid fields from
    /// appearing in the assistant. The strict save parser remains separate.
    /// Repeated parseable rows are previewed in their authored (last-wins)
    /// order; strict validation still diagnoses duplicates.
    /// </summary>
    public static IReadOnlyDictionary<string, FilterParameterValue> PreviewStage(
        FilterInstance selected, string original, int stageId)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(original);
        FilterStageDefinition stage = selected.Definition.GetStage(stageId);
        Dictionary<string, FilterParameterValue> values =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(original))
        {
            if (!TryAssignment(line, stage.Name, out string parameter, out int equals))
                continue;
            FilterParameterDescriptor? descriptor =
                FilterCatalogue.GetParameters(stage.Kind).FirstOrDefault(p =>
                    string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
                continue;
            string raw = line[(equals + 1)..].Trim();
            try
            {
                FilterParameterValue parsed;
                if (descriptor.Kind == FilterParameterValueKind.Number)
                {
                    if (!double.TryParse(raw, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double number))
                        continue;
                    parsed = FilterParameterValue.FromNumber(number);
                }
                else
                    parsed = FilterParameterValue.FromChoice(raw);
                values[descriptor.Name] = descriptor.Normalize(parsed);
            }
            catch (ArgumentException)
            {
                // Invalid fields do not suppress valid preview fields.
            }
        }
        return values;
    }

    /// <summary>
    /// Upsert the assistant's stage parameters into the current authored text.
    /// Each existing stage.parameter row is edited where it stands (retaining
    /// its original path spelling and spacing). Absent parameters are inserted
    /// after the last row for that stage. Unrelated rows, annotations, malformed
    /// lines and ordering are not owned or rearranged by this helper.
    /// A new section comment is added only if this stage had no rows and
    /// did not already have a heading. The operation is idempotent.
    /// </summary>
    public static string RewriteStage(
        FilterInstance selected, string original, int stageId,
        IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments,
        string annotation)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(annotation);
        FilterStageDefinition stage = selected.Definition.GetStage(stageId);
        KeyValuePair<string, FilterParameterValue>[] batch = assignments.ToArray();
        FilterInstance validation = selected.Clone();
        validation.SetStageParameters(stageId, batch);

        Dictionary<string, string> printed = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, FilterParameterValue _) in batch)
        {
            FilterParameterDescriptor descriptor =
                FilterCatalogue.GetParameters(stage.Kind).First(p =>
                    string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (printed.ContainsKey(descriptor.Name))
                throw new ArgumentException(
                    $"Duplicate wizard assignment for {stage.Name}.{descriptor.Name}.");
            FilterParameterValue value = validation.GetStageParameters(stageId)
                .GetParameter(descriptor.Name);
            printed.Add(descriptor.Name, value.Kind == FilterParameterValueKind.Number
                ? value.NumericValue.ToString("G17", CultureInfo.InvariantCulture)
                : value.ChoiceValue!);
        }

        string newline = original.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n" : original.Contains('\r') ? "\r" : "\n";
        List<string> lines = Lines(original).ToList();
        HashSet<string> updated = new(StringComparer.OrdinalIgnoreCase);
        bool hasStageRows = false;
        bool hasStageHeading = false;
        int insertion = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string text = lines[i];
            if (IsStageHeading(text, stage.Name))
            {
                hasStageHeading = true;
                if (insertion < 0)
                    insertion = i + 1;
            }
            if (!TryAssignment(text, stage.Name, out string parameter, out int equals))
                continue;

            hasStageRows = true;
            insertion = i + 1;
            if (!printed.TryGetValue(parameter, out string? replacement))
                continue;
            // Keep the typed path and surrounding whitespace; only the
            // actual value changes, so comments/other formatting survive.
            string suffix = text[(equals + 1)..];
            int lead = suffix.Length - suffix.TrimStart().Length;
            int tail = suffix.Length - suffix.TrimEnd().Length;
            lines[i] = text[..(equals + 1)] + suffix[..lead]
                + replacement + (tail == 0 ? "" : suffix[^tail..]);
            updated.Add(parameter);
        }

        List<string> additions = [];
        if (!hasStageRows && !hasStageHeading)
            additions.Add($"# {stage.Name}: {annotation.Trim()}");
        foreach ((string name, string value) in printed)
            if (!updated.Contains(name))
                additions.Add($"{stage.Name}.{name} = {value}");
        if (additions.Count == 0)
            return original;

        if (insertion >= 0)
            lines.InsertRange(insertion, additions);
        else
        {
            // Keep the existing final newline, if any, and avoid inserting
            // a fresh stage heading in the middle of unrelated assignments.
            if (lines.Count == 1 && lines[0].Length == 0)
                lines.Clear();
            else if (lines.Count > 0 && lines[^1].Length == 0)
                lines.Insert(lines.Count - 1, "");
            else if (lines.Count > 0)
                lines.Add("");
            lines.AddRange(additions);
        }
        return string.Join(newline, lines);
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static bool IsStageHeading(string line, string stageName)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
            return false;
        string rest = trimmed[1..].TrimStart();
        int colon = rest.IndexOf(':');
        return colon >= 0 && string.Equals(rest[..colon].Trim(),
            stageName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAssignment(string line, string stageName,
        out string parameter, out int equals)
    {
        parameter = "";
        equals = -1;
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
            return false;
        equals = line.IndexOf('=');
        if (equals < 0 || line.IndexOf('=', equals + 1) >= 0)
            return false;
        string path = line[..equals].Trim();
        int dot = path.IndexOf('.');
        if (dot <= 0 || dot != path.LastIndexOf('.') || dot == path.Length - 1
            || !string.Equals(path[..dot].Trim(),
                stageName, StringComparison.OrdinalIgnoreCase))
            return false;
        parameter = path[(dot + 1)..].Trim();
        return parameter.Length > 0;
    }

    private static ArgumentException Invalid(int zeroBasedLine, string issue)
        => new($"Line {zeroBasedLine + 1}: {issue}", "text");
}
