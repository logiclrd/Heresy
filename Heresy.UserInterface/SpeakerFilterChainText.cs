using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

namespace Heresy.UserInterface;

/// <summary>
/// Self-contained inline speaker Filter authoring. Each non-comment line
/// declares one serial stage: Kind Parameter=Value ... . Kind and parameter
/// choices are the ordinary reusable Filter catalogue; this is never a
/// reference to a mutable Filter SongObject. No song schema is affected.
/// </summary>
public static class SpeakerFilterChainText
{
    public const string Default = "OnePole CutoffHz=1000 Mode=LowPass";

    public static FilterInstance Parse(string text, int sampleRate, int speakerIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (speakerIndex < 0 || speakerIndex >= 32)
            throw new ArgumentOutOfRangeException(nameof(speakerIndex));
        List<FilterStageDefinition> stages = [];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] fields = line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!Enum.TryParse(fields[0], ignoreCase: true, out FilterKind kind)
                || !Enum.IsDefined(kind))
                throw new ArgumentException($"Unknown or unavailable speaker Filter type '{fields[0]}'.");
            FilterSpecificationInstance parameters = new(kind);
            List<KeyValuePair<string, FilterParameterValue>> assignments = [];
            for (int k = 1; k < fields.Length; k++)
            {
                int eq = fields[k].IndexOf('=');
                if (eq <= 0 || eq == fields[k].Length - 1)
                    throw new ArgumentException(
                        $"Use Parameter=Value for speaker Filter stage '{fields[0]}'.");
                string name = fields[k][..eq];
                string value = fields[k][(eq + 1)..];
                FilterParameterDescriptor descriptor =
                    parameters.Parameters.FirstOrDefault(d =>
                        string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"Unknown {kind} parameter '{name}'.");
                FilterParameterValue v = descriptor.Kind == FilterParameterValueKind.Choice
                    ? FilterParameterValue.FromChoice(value)
                    : double.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double number)
                        ? FilterParameterValue.FromNumber(number)
                        : throw new ArgumentException(
                            $"Invalid numeric value '{value}' for {name}.");
                assignments.Add(new(descriptor.Name, v));
            }
            if (stages.Count >= 32)
                throw new ArgumentException("Speaker Filter chain exceeds 32 serial stages.");
            parameters.SetParameters(assignments);
            stages.Add(new FilterStageDefinition(
                stages.Count + 1, "stage" + (stages.Count + 1),
                kind, parameters.Parameters.Select(d =>
                    new KeyValuePair<string, FilterParameterValue>(
                        d.Name, parameters.GetParameter(d.Name)))));
        }
        if (stages.Count == 0)
            throw new ArgumentException("A reusable speaker Filter must have at least one stage.");
        FilterDefinition definition = new(
            (ObjectId)(uint)(0x7E000000 + speakerIndex),
            "InlineSpeakerFilter", stages);
        FilterInstance instance = new(definition);
        // Stage preparation is a control-side operation. Validate against
        // actual Nyquist, structural delay, convolution budgets, etc.
        _ = new StreamingFilterChain(new FilterChain([instance]), sampleRate, 1);
        return instance;
    }

    public static string Format(FilterInstance filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        StringBuilder builder = new();
        foreach (ConfiguredFilterStage stage in filter.GetStages())
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(stage.Kind);
            foreach (FilterParameterDescriptor parameter in stage.Parameters.Parameters)
            {
                FilterParameterValue value = stage.Parameters.GetParameter(parameter.Name);
                builder.Append(' ').Append(parameter.Name).Append('=');
                builder.Append(value.Kind == FilterParameterValueKind.Choice
                    ? value.ChoiceValue
                    : value.NumericValue.ToString("G17", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }
}
