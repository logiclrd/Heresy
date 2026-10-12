using System;
using Heresy.Core.Filters;
using Heresy.Render.Filters;

namespace Heresy.Render.Playback;

/// <summary>
/// One durable channel-output DSP slot, shared by all voices actually
/// sounding on the same host. Unlike per-note layers, it is not reset by
/// note starts, NNA displacement, scope completion, or source cancellation.
/// Output speaker filtering is a later, separate stage.
/// </summary>
internal sealed class ChannelOutputFilterState
{
    private readonly StreamingFilterChain _chain;
    private readonly FilterInstance _configuration;

    private ChannelOutputFilterState(
        FilterInstance configuration, StreamingFilterChain chain, int remaining)
    {
        _configuration = configuration;
        _chain = chain;
        _tailRemainingFrames = remaining;
    }

    /// <summary>Displaced NNA hosts begin with an exact history copy at
    /// the displacement frame, independent from the physical channel's
    /// continuing filter slot.</summary>
    public ChannelOutputFilterState Fork()
        => new(_configuration.Clone(), _chain.Fork(), _tailRemainingFrames);
    private int _tailRemainingFrames;

    public ChannelOutputFilterState(
        FilterInstance configuration, int sampleRate, int outputs)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration.Clone();
        _chain = new StreamingFilterChain(
            new FilterChain(new[] { _configuration }), sampleRate, outputs);
    }

    /// <summary>Prepare a complete, isolated successor for the specified
    /// typed assignment batch. Any failure leaves this state unchanged;
    /// compatible histories and smoothing in the fork are retained.</summary>
    public ChannelOutputFilterState PrepareParameterUpdate(
        int stageId,
        System.Collections.Generic.IReadOnlyList<
            System.Collections.Generic.KeyValuePair<string, FilterParameterValue>> assignments)
    {
        FilterInstance updated = _configuration.Clone();
        updated.SetStageParameters(stageId, assignments);
        StreamingFilterChain fork = _chain.Fork();
        fork.Configure(new FilterChain(new[] { updated }));
        return new ChannelOutputFilterState(updated, fork, _tailRemainingFrames);
    }

    public FilterInstance SnapshotConfiguration() => _configuration.Clone();

    public bool HasTail => _tailRemainingFrames > 0;
    public int PreparedTailFrames => _chain.PreparedTailFrames;

    /// <summary>
    /// Always filter the *summed* host PCM. Advancing silent frames keeps
    /// finite FIR/IIR tails continuous across buffer boundaries and source
    /// changes. In the absence of input, quiescence is bounded by the
    /// prepared filter tail rather than by arbitrary source lifetimes.
    /// </summary>
    public void ProcessInterleaved(Span<float> buffer)
    {
        int channels = _chain.OutputChannelCount;
        if (buffer.Length % channels != 0)
            throw new ArgumentException("Channel output PCM must contain whole frames.",
                nameof(buffer));

        for (int position = 0; position < buffer.Length; position += channels)
        {
            Span<float> frame = buffer.Slice(position, channels);
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
    }
}
