using System;
using System.Collections.Generic;
using Heresy.Core.Filters;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;

namespace Heresy.Render.Playback;

/// <summary>
/// One durable channel-output DSP slot, shared by all voices actually
/// sounding on the same host. Its parameter envelopes have independent
/// absolute channel clocks, not note-local Note Off/release lifetimes.
/// </summary>
internal sealed class ChannelOutputFilterState
{
    private readonly record struct BindingSpec(
        IEnvelopeCurve Curve, long StartFrame);

    private readonly StreamingFilterChain _chain;
    private readonly FilterInstance _configuration;
    private readonly int _sampleRate;
    private readonly Dictionary<(int StageId, string Parameter), BindingSpec>
        _bindings;
    private NoteFilterEnvelopeRealtimeControls? _realtime;
    private readonly Action<string> _reportWarning;
    private Action<string>? _warningSink;
    private bool _warningReported;
    private int _tailRemainingFrames;

    private ChannelOutputFilterState(
        FilterInstance configuration, StreamingFilterChain chain, int remaining,
        int sampleRate,
        Dictionary<(int StageId, string Parameter), BindingSpec> bindings)
    {
        _configuration = configuration;
        _chain = chain;
        _tailRemainingFrames = remaining;
        _sampleRate = sampleRate;
        _bindings = new(bindings);
        _reportWarning = ReportWarning;
        RebuildRealtimeBindings();
    }

    public ChannelOutputFilterState(
        FilterInstance configuration, int sampleRate, int outputs)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration.Clone();
        _sampleRate = sampleRate;
        _bindings = [];
        _chain = new StreamingFilterChain(
            new FilterChain([_configuration]), sampleRate, outputs);
        _reportWarning = ReportWarning;
    }

    /// <summary>Fork audio history and persistent envelope bindings. The
    /// new host owns its clocks even after the original host changes.</summary>
    public ChannelOutputFilterState Fork()
        => new(_configuration.Clone(), _chain.Fork(),
            _tailRemainingFrames, _sampleRate, _bindings);

    public ChannelOutputFilterState PrepareParameterUpdate(
        int stageId,
        IReadOnlyList<KeyValuePair<string, FilterParameterValue>> assignments)
    {
        FilterInstance updated = _configuration.Clone();
        updated.SetStageParameters(stageId, assignments);
        StreamingFilterChain fork = _chain.Fork();
        fork.Configure(new FilterChain([updated]));
        return new ChannelOutputFilterState(updated, fork,
            _tailRemainingFrames, _sampleRate, _bindings);
    }

    public ChannelOutputFilterState PrepareTextUpdate(string text)
    {
        FilterInstance updated = FilterAssignmentText.ApplyToClone(
            _configuration, text);
        StreamingFilterChain fork = _chain.Fork();
        fork.Configure(new FilterChain([updated]));
        return new ChannelOutputFilterState(updated, fork,
            _tailRemainingFrames, _sampleRate, _bindings);
    }

    /// <summary>Prepare and validate one complete channel binding edit
    /// without mutating the existing Filter or its convolution histories.
    /// Null curve detaches and exposes the latest independently authored
    /// base parameter. Binding IDs belong to this host, not a note.</summary>
    public ChannelOutputFilterState PrepareParameterEnvelopeUpdate(
        int stageId, string parameter, IEnvelopeCurve? curve, long frame)
    {
        FilterStageDefinition stage = _configuration.Definition.GetStage(stageId);
        FilterParameterDescriptor? descriptor = null;
        foreach (FilterParameterDescriptor candidate in
            FilterCatalogue.GetParameters(stage.Kind))
            if (string.Equals(candidate.Name, parameter,
                StringComparison.OrdinalIgnoreCase))
            {
                descriptor = candidate;
                break;
            }
        if (descriptor is null)
            throw new ArgumentException("Unknown channel Filter stage parameter.");
        if (!descriptor.IsModulatable)
            throw new ArgumentException("Channel Filter parameter is structural or nonnumeric.");

        // Reject invalid first samples at the command boundary rather than
        // leaving a half-attached binding waiting for the PCM loop.
        if (curve is not null)
        {
            EnvelopePlaybackState initial = new(curve, frame, _sampleRate);
            descriptor.Normalize(FilterParameterValue.FromNumber(
                initial.GetValue(frame)));
        }

        Dictionary<(int StageId, string Parameter), BindingSpec> updated =
            new(_bindings);
        var key = (stageId, descriptor.Name);
        if (curve is null)
            updated.Remove(key);
        else
            updated[key] = new BindingSpec(curve, frame);

        // Reset the successor's effective configuration to its authored
        // base before installing the new binding set. In particular, a
        // detached parameter must not leave its previous envelope override
        // latched forever in the forked DSP coefficients.
        StreamingFilterChain fork = _chain.Fork();
        fork.Configure(new FilterChain([_configuration]));
        return new ChannelOutputFilterState(_configuration.Clone(),
            fork, _tailRemainingFrames, _sampleRate, updated);
    }

    private void RebuildRealtimeBindings()
    {
        if (_bindings.Count == 0)
        {
            _realtime = null;
            return;
        }
        NoteFilterEnvelopeRealtimeControls controls = new(
            new FilterChain([_configuration]), _sampleRate);
        List<NoteFilterEnvelopeRealtimeControls.Binding> bindings = [];
        foreach (var entry in _bindings)
            bindings.Add(new NoteFilterEnvelopeRealtimeControls.Binding(
                0, entry.Key.StageId, entry.Key.Parameter,
                new EnvelopePlaybackState(
                    entry.Value.Curve, entry.Value.StartFrame, _sampleRate)));
        controls.SetBindings(bindings);
        _realtime = controls;
    }

    public bool TryPreparePendingFirs()
        => _realtime?.TryPreparePendingFirs() ?? false;

    public void AdoptPreparedFirs() => _realtime?.AdoptPreparedFirs();

    public void QueuePendingFirs(FirPreparationScheduler scheduler)
        => _realtime?.QueuePendingFirs(scheduler);

    public FilterInstance SnapshotConfiguration() => _configuration.Clone();

    public bool HasTail => _tailRemainingFrames > 0;
    public int PreparedTailFrames => _chain.PreparedTailFrames;

    private void ReportWarning(string message)
    {
        if (_warningReported || _warningSink is null) return;
        _warningReported = true;
        _warningSink(message);
    }

    /// <summary>Filter the summed host feed once. The caller passes its
    /// absolute session frame so per-host envelope clocks do not restart
    /// on a note start, cancellation or PCM chunk boundary.</summary>
    public void ProcessInterleaved(Span<float> buffer,
        long absoluteStartFrame, Action<string>? warning)
    {
        int channels = _chain.OutputChannelCount;
        if (buffer.Length % channels != 0)
            throw new ArgumentException(
                "Channel output PCM must contain whole frames.", nameof(buffer));
        if (absoluteStartFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteStartFrame));

        _warningSink = warning;
        for (int position = 0; position < buffer.Length; position += channels)
        {
            Span<float> frame = buffer.Slice(position, channels);
            _realtime?.Apply(
                checked(absoluteStartFrame + position / channels),
                _chain, _reportWarning);
            bool hasInput = false;
            for (int c = 0; c < channels; c++)
                if (frame[c] != 0f)
                {
                    hasInput = true;
                    break;
                }

            _chain.ProcessFrame(frame);
            if (hasInput)
                _tailRemainingFrames = _chain.PreparedTailFrames;
            else if (_tailRemainingFrames > 0)
                _tailRemainingFrames--;
        }
        _warningSink = null;
    }

    // Kept for callers that configure an unmodulated host directly.
    public void ProcessInterleaved(Span<float> buffer)
        => ProcessInterleaved(buffer, 0, null);
}
