using System;
using System.Collections.Generic;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Frozen preparation-time cache of immutable Filter stage plans for declared
/// data and scripted Instrument tones. Prewarming may design expensive FIR/FFT
/// kernels, and MUST run before the PCM producer starts. Scripted Instruments
/// select from a finite authored tone table, so all of their configurations
/// can be prewarmed without a runtime request queue. The sealed cache is
/// read-only: no unexpected configuration can silently trigger plan design
/// inside the audio producer. A per-invocation StreamingFilterChain
/// still creates its own mutable DSP histories from the cached plans.
/// </summary>
public sealed class PreparedToneFilterStageFactory : IStreamingFilterStageFactory
{
    private sealed record Entry(PreparedFilterStage Stage, IStreamingFilterStagePlan Plan);
    private readonly List<Entry> _building = [];
    private Entry[]? _published;

    public PreparedToneFilterStageFactory(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Producer-only preparation, before Freeze().</summary>
    public void Prewarm(FilterInstance configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_published is not null)
            throw new InvalidOperationException("Filter plan cache is already frozen.");

        int stageIndex = 0;
        foreach (ConfiguredFilterStage resolved in configuration.GetStages())
        {
            Dictionary<string, FilterParameterValue> values =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (FilterParameterDescriptor descriptor in resolved.Parameters.Parameters)
                values.Add(descriptor.Name,
                    resolved.Parameters.GetParameter(descriptor.Name));
            PreparedFilterStage request = new(0, stageIndex++,
                configuration.DefinitionId, resolved.StageId, resolved.Kind,
                configuration.GetStageGenerationForPersistence(resolved.StageId),
                values);
            if (Find(_building, request) is not null)
                continue;

            // This is the *only* call to a potentially expensive built-in
            // factory, and never happens after Freeze().
            IStreamingFilterStagePlan plan =
                BuiltInFilterStageFactory.Instance.Prepare(request, SampleRate);
            _building.Add(new Entry(request, plan));
        }
    }

    public void Freeze()
    {
        if (_published is not null)
            return;
        _published = _building.ToArray();
        _building.Clear();
    }

    /// <summary>Safe at a note/event boundary or during PCM: no coefficients,
    /// FIR windows or transforms are computed here.</summary>
    public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (sampleRate != SampleRate)
            throw new InvalidOperationException(
                "Tone Filter plans are bound to their prepared output sample rate.");
        Entry[] published = _published ?? throw new InvalidOperationException(
            "Tone Filter plans must be frozen before audio playback.");
        // Normalize only the serial layer ordinal: the same definition stage
        // can appear at multiple nested Instrument levels without changing
        // its coefficients. Keep stage identity/generation/parameter checks.
        PreparedFilterStage normalized = new(0, stage.StageIndex,
            stage.DefinitionId, stage.StageId, stage.Kind, stage.Generation,
            stage.Parameters);
        return Find(published, normalized)?.Plan
            ?? throw new NotSupportedException(
                "The selected tone Filter has parameters not prepared in the " +
                "frozen playback snapshot. Filter design cannot run on " +
                "the audio producer thread.");
    }

    private static Entry? Find(IEnumerable<Entry> entries, PreparedFilterStage request)
    {
        foreach (Entry entry in entries)
            if (entry.Stage.HasSameParameters(request))
                return entry;
        return null;
    }
}
