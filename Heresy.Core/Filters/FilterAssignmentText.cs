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
    /// Deterministic assistant/wizard rewrite: remove *all* assignments to
    /// the requested stage and its # stageName: annotations, preserving the
    /// exact original text of every other line (including stage20). The new
    /// block is inserted at the old stage's first position, or appended.
    /// Rewriting an already generated block with the same content is stable.
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
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> block =
        [
            $"# {stage.Name}: {annotation.Trim()}",
        ];
        foreach ((string name, FilterParameterValue _) in batch)
        {
            FilterParameterDescriptor descriptor =
                FilterCatalogue.GetParameters(stage.Kind).First(p =>
                    string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (!used.Add(descriptor.Name))
                throw new ArgumentException(
                    $"Duplicate wizard assignment for {stage.Name}.{descriptor.Name}.");
            FilterParameterValue value = validation.GetStageParameters(stageId)
                .GetParameter(descriptor.Name);
            string printed = value.Kind == FilterParameterValueKind.Number
                ? value.NumericValue.ToString("G17", CultureInfo.InvariantCulture)
                : value.ChoiceValue!;
            block.Add($"{stage.Name}.{descriptor.Name} = {printed}");
        }

        string newline = original.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n" : "\n";
        string[] lines = original.Replace("\r\n", "\n")
            .Replace('\r', '\n').Split('\n');
        List<string> result = [];
        int insertion = -1;
        foreach (string line in lines)
        {
            if (MatchesStage(line, stage.Name))
            {
                if (insertion < 0) insertion = result.Count;
                continue;
            }
            result.Add(line);
        }
        if (insertion < 0)
        {
            // Avoid accumulating empty spacer lines during append.
            while (result.Count > 0 && result[^1].Length == 0)
                result.RemoveAt(result.Count - 1);
            if (result.Count > 0)
                result.Add("");
            insertion = result.Count;
        }
        result.InsertRange(insertion, block);
        return string.Join(newline, result).TrimEnd('\r', '\n');
    }

    private static bool MatchesStage(string line, string stageName)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            string rest = trimmed[1..].TrimStart();
            int colon = rest.IndexOf(':');
            return colon >= 0 && string.Equals(rest[..colon].Trim(),
                stageName, StringComparison.OrdinalIgnoreCase);
        }
        int equals = trimmed.IndexOf('=');
        int dot = trimmed.IndexOf('.');
        return dot > 0 && equals > dot
            && string.Equals(trimmed[..dot].Trim(), stageName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static ArgumentException Invalid(int zeroBasedLine, string issue)
        => new($"Line {zeroBasedLine + 1}: {issue}", "text");
}
