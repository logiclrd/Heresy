using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Invocation-owned incremental serial filter chain. A single instance is
/// never shared by unrelated notes, output hosts or private mixdowns.
/// ProcessFrame and ProcessInterleaved advance each stage's per-channel
/// history exactly once for each chronological frame. They allocate nothing
/// and perform no DSP preparation in the processing loop.
/// </summary>
public sealed class StreamingFilterChain
{
	private sealed class RunningStage
	{
		public required PreparedFilterStage Stage { get; init; }
		public required IStreamingFilterStagePlan Plan { get; init; }
		public required IStreamingFilterStageState[] Channels { get; init; }

		public RunningStage Clone() => new()
		{
			Stage = Stage,
			Plan = Plan,
			Channels = Channels.Select(state => state.Clone()).ToArray(),
		};
	}

	private readonly IStreamingFilterStageFactory _factory;
	private RunningStage[] _stages = [];
	private int _tailBudgetFrames;
	private int _preparedLatencyFrames;
	private int _tailRemainingFrames;
	private bool _inputFinished;

	public StreamingFilterChain(
		FilterChain chain, int sampleRate, int outputChannelCount,
		IStreamingFilterStageFactory? factory = null,
		double maximumTailSeconds = 10.0)
	{
		if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));
		if (!double.IsFinite(maximumTailSeconds) || maximumTailSeconds < 0
			|| maximumTailSeconds * sampleRate > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumTailSeconds));

		SampleRate = sampleRate;
		OutputChannelCount = outputChannelCount;
		_factory = factory ?? BuiltInFilterStageFactory.Instance;
		MaximumTailFrames = checked((int)Math.Ceiling(maximumTailSeconds * sampleRate));
		Configure(chain);
	}

	private StreamingFilterChain(StreamingFilterChain other)
	{
		SampleRate = other.SampleRate;
		OutputChannelCount = other.OutputChannelCount;
		MaximumTailFrames = other.MaximumTailFrames;
		_factory = other._factory;
		_stages = other._stages.Select(stage => stage.Clone()).ToArray();
		_tailBudgetFrames = other._tailBudgetFrames;
		_preparedLatencyFrames = other._preparedLatencyFrames;
		_tailRemainingFrames = other._tailRemainingFrames;
		_inputFinished = other._inputFinished;
	}

	public int SampleRate { get; }
	public int OutputChannelCount { get; }

	/// <summary>Maximum allowed trailing silence frames after FinishInput,
	/// not a measure of actual impulse response.</summary>
	public int MaximumTailFrames { get; }

	/// <summary>Finite upper bound from the sum of serial-stage tails,
	/// clamped to the configured per-invocation maximum.</summary>
	public int PreparedTailFrames => _tailBudgetFrames;

	/// <summary>Fixed serial group delay in output frames for prepared
	/// stages that explicitly report latency (currently linear-phase FIR).
	/// This does not automatically align or delay a host's dry path.</summary>
	public int PreparedLatencyFrames => _preparedLatencyFrames;
	public int TailRemainingFrames => _tailRemainingFrames;
	public bool IsInputFinished => _inputFinished;
	public int StageCount => _stages.Length;

	/// <summary>
	/// Prepares and validates all stages before publishing any changes.
	/// Compatible stages preserve histories, with parameter reconfiguration
	/// performed on deep-cloned state; incompatible stages start fresh.
	/// Changing order, kind/generation, structural parameters or output
	/// topology is incompatible. Identical plans leave running states alone.
	/// Reconfigure is not permitted after FinishInput until Reset.
	/// </summary>
	public void Configure(FilterChain chain)
	{
		ArgumentNullException.ThrowIfNull(chain);
		if (_inputFinished)
			throw new InvalidOperationException(
				"Reset before reconfiguring a finished Filter chain.");

		List<RunningStage> prepared = [];
		int layerIndex = 0;
		foreach (FilterInstance instance in chain.Layers)
		{
			int stageIndex = 0;
			foreach (ConfiguredFilterStage resolved in instance.GetStages())
			{
				FilterSpecificationInstance parameters = resolved.Parameters;
				Dictionary<string, FilterParameterValue> values =
					new(StringComparer.OrdinalIgnoreCase);
				foreach (FilterParameterDescriptor descriptor in parameters.Parameters)
					values.Add(descriptor.Name, parameters.GetParameter(descriptor.Name));
				PreparedFilterStage stage = new(
					layerIndex, stageIndex, instance.DefinitionId, resolved.StageId,
					resolved.Kind, instance.GetStageGenerationForPersistence(
						resolved.StageId), values);
				IStreamingFilterStagePlan plan = _factory.Prepare(stage, SampleRate)
					?? throw new InvalidOperationException(
						"Filter factory returned a null prepared plan.");
				if (plan.TailFrames < 0)
					throw new InvalidOperationException(
						"Filter factory returned a negative tail bound.");

				// Read but never mutate published channel histories. Failed
				// preparation or clone reconfiguration cannot alter the chain.
				int ordinal = prepared.Count;
				RunningStage? previous = ordinal < _stages.Length
					? _stages[ordinal] : null;
				bool compatible = previous is not null
					&& stage.IsHistoryCompatibleWith(previous.Stage);
				bool same = compatible && stage.HasSameParameters(previous!.Stage);
				IStreamingFilterStageState[] channels;
				if (same)
					channels = previous!.Channels;
				else if (compatible)
				{
					channels = previous!.Channels.Select(state => state.Clone()).ToArray();
					foreach (IStreamingFilterStageState channel in channels)
						channel.Reconfigure(plan);
				}
				else
				{
					channels = new IStreamingFilterStageState[OutputChannelCount];
					for (int channel = 0; channel < channels.Length; channel++)
						channels[channel] = plan.CreateState()
							?? throw new InvalidOperationException(
								"Filter plan returned a null channel state.");
				}

				prepared.Add(new RunningStage
				{
					Stage = stage,
					Plan = plan,
					Channels = channels,
				});
				stageIndex++;
			}
			layerIndex++;
		}

		long requiredTail = 0;
		long requiredLatency = 0;
		foreach (RunningStage item in prepared)
		{
			requiredTail = Math.Min((long)MaximumTailFrames,
				requiredTail + item.Plan.TailFrames);
			if (item.Plan is ILatencyAwareFilterStagePlan latency)
			{
				if (latency.LatencyFrames < 0)
					throw new InvalidOperationException(
						"Filter plan returned negative latency.");
				requiredLatency += latency.LatencyFrames;
				if (requiredLatency > int.MaxValue)
					throw new InvalidOperationException(
						"Total filter-chain latency exceeds integer frame range.");
			}
		}
		_stages = prepared.ToArray(); // publish only after all work succeeded
		_tailBudgetFrames = (int)requiredTail;
		_preparedLatencyFrames = (int)requiredLatency;
		_tailRemainingFrames = 0;
	}

	/// <summary>
	/// Preflight every affected TPT, Audio EQ Biquad, Ladder, All-Pass,
	/// feedback Comb and feedforward Comb stage before applying numeric changes. Mixed-family updates remain
	/// atomic, including
	/// invalid Nyquist, shelving and stability constraints. Caller-owned
	/// scratch and prepared states keep this path allocation-free.
	/// </summary>
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch,
		ReadOnlySpan<RealtimeLinearLadderStageUpdate> ladderUpdates,
		Span<RealtimeLinearLadderCoefficients> ladderScratch,
		ReadOnlySpan<RealtimeSaturatedLadderStageUpdate> saturatedUpdates,
		Span<RealtimeSaturatedLadderCoefficients> saturatedScratch,
		ReadOnlySpan<RealtimeAllPassStageUpdate> allPassUpdates,
		Span<RealtimeAllPassCoefficient> allPassScratch,
		ReadOnlySpan<RealtimeFeedbackCombStageUpdate> combUpdates,
		Span<RealtimeFeedbackCombCoefficients> combScratch,
		ReadOnlySpan<RealtimeFeedforwardCombStageUpdate> feedforwardUpdates,
		Span<RealtimeFeedforwardCombCoefficients> feedforwardScratch)
	{
		if (_inputFinished || tptScratch.Length < tptUpdates.Length
			|| biquadScratch.Length < biquadUpdates.Length
			|| ladderScratch.Length < ladderUpdates.Length
			|| saturatedScratch.Length < saturatedUpdates.Length
			|| allPassScratch.Length < allPassUpdates.Length
			|| combScratch.Length < combUpdates.Length
			|| feedforwardScratch.Length < feedforwardUpdates.Length)
			return false;

		for (int i = 0; i < tptUpdates.Length; i++)
		{
			RealtimeTptStageUpdate update = tptUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeTptFilterState runtime
				|| !runtime.TryPrepareRealtime(
					update.CutoffHz, update.Q, SampleRate, out tptScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < biquadUpdates.Length; i++)
		{
			RealtimeBiquadStageUpdate update = biquadUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeBiquadFilterState runtime
				|| !runtime.TryPrepareRealtime(update.CutoffHz, update.Q,
					update.ShelfSlope, update.GainDb, SampleRate,
					out biquadScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < ladderUpdates.Length; i++)
		{
			RealtimeLinearLadderStageUpdate update = ladderUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeLinearLadderFilterState runtime
				|| !runtime.TryPrepareRealtime(update.CutoffHz,
					update.Resonance, SampleRate, out ladderScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (ladderUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < biquadUpdates.Length; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < saturatedUpdates.Length; i++)
		{
			RealtimeSaturatedLadderStageUpdate update = saturatedUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeSaturatedLadderFilterState runtime
				|| !runtime.TryPrepareRealtime(update.CutoffHz,
					update.Resonance, update.Drive, SampleRate,
					out saturatedScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (saturatedUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < biquadUpdates.Length; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < ladderUpdates.Length; j++)
				if (ladderUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < allPassUpdates.Length; i++)
		{
			RealtimeAllPassStageUpdate update = allPassUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeAllPassFilterState runtime
				|| !runtime.TryPrepareRealtime(
					update.CutoffHz, SampleRate, out allPassScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (allPassUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < biquadUpdates.Length; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < ladderUpdates.Length; j++)
				if (ladderUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < saturatedUpdates.Length; j++)
				if (saturatedUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < combUpdates.Length; i++)
		{
			RealtimeFeedbackCombStageUpdate update = combUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeFeedbackCombFilterState runtime
				|| !runtime.TryPrepareRealtime(
					update.Feedback, update.Mix, out combScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (combUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < biquadUpdates.Length; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < ladderUpdates.Length; j++)
				if (ladderUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < saturatedUpdates.Length; j++)
				if (saturatedUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < allPassUpdates.Length; j++)
				if (allPassUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < feedforwardUpdates.Length; i++)
		{
			RealtimeFeedforwardCombStageUpdate update = feedforwardUpdates[i];
			if ((uint)update.StageIndex >= (uint)_stages.Length
				|| _stages[update.StageIndex].Channels[0]
					is not IRealtimeFeedforwardCombFilterState runtime
				|| !runtime.TryPrepareRealtime(
					update.Feedforward, update.Mix, out feedforwardScratch[i]))
				return false;
			for (int j = 0; j < i; j++)
				if (feedforwardUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < tptUpdates.Length; j++)
				if (tptUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < biquadUpdates.Length; j++)
				if (biquadUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < ladderUpdates.Length; j++)
				if (ladderUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < saturatedUpdates.Length; j++)
				if (saturatedUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < allPassUpdates.Length; j++)
				if (allPassUpdates[j].StageIndex == update.StageIndex)
					return false;
			for (int j = 0; j < combUpdates.Length; j++)
				if (combUpdates[j].StageIndex == update.StageIndex)
					return false;
		}

		for (int i = 0; i < tptUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[tptUpdates[i].StageIndex].Channels)
				((IRealtimeTptFilterState)channel).CommitRealtime(tptScratch[i]);
		}
		for (int i = 0; i < biquadUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[biquadUpdates[i].StageIndex].Channels)
				((IRealtimeBiquadFilterState)channel)
					.CommitRealtime(biquadScratch[i]);
		}
		for (int i = 0; i < ladderUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[ladderUpdates[i].StageIndex].Channels)
				((IRealtimeLinearLadderFilterState)channel)
					.CommitRealtime(ladderScratch[i]);
		}
		for (int i = 0; i < saturatedUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[saturatedUpdates[i].StageIndex].Channels)
				((IRealtimeSaturatedLadderFilterState)channel)
					.CommitRealtime(saturatedScratch[i]);
		}
		for (int i = 0; i < allPassUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[allPassUpdates[i].StageIndex].Channels)
				((IRealtimeAllPassFilterState)channel)
					.CommitRealtime(allPassScratch[i]);
		}
		for (int i = 0; i < combUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[combUpdates[i].StageIndex].Channels)
				((IRealtimeFeedbackCombFilterState)channel)
					.CommitRealtime(combScratch[i]);
		}
		for (int i = 0; i < feedforwardUpdates.Length; i++)
		{
			foreach (IStreamingFilterStageState channel in
				_stages[feedforwardUpdates[i].StageIndex].Channels)
				((IRealtimeFeedforwardCombFilterState)channel)
					.CommitRealtime(feedforwardScratch[i]);
		}
		return true;
	}

	// Retain previous fast-family API for internal callers.
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch,
		ReadOnlySpan<RealtimeLinearLadderStageUpdate> ladderUpdates,
		Span<RealtimeLinearLadderCoefficients> ladderScratch,
		ReadOnlySpan<RealtimeSaturatedLadderStageUpdate> saturatedUpdates,
		Span<RealtimeSaturatedLadderCoefficients> saturatedScratch,
		ReadOnlySpan<RealtimeAllPassStageUpdate> allPassUpdates,
		Span<RealtimeAllPassCoefficient> allPassScratch,
		ReadOnlySpan<RealtimeFeedbackCombStageUpdate> combUpdates,
		Span<RealtimeFeedbackCombCoefficients> combScratch)
		=> TryApplyRealtimeNumericUpdates(tptUpdates, tptScratch,
			biquadUpdates, biquadScratch, ladderUpdates, ladderScratch,
			saturatedUpdates, saturatedScratch, allPassUpdates, allPassScratch,
			combUpdates, combScratch,
			ReadOnlySpan<RealtimeFeedforwardCombStageUpdate>.Empty,
			Span<RealtimeFeedforwardCombCoefficients>.Empty);

	// Keep the previous five-family call contract for internal users.
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch,
		ReadOnlySpan<RealtimeLinearLadderStageUpdate> ladderUpdates,
		Span<RealtimeLinearLadderCoefficients> ladderScratch,
		ReadOnlySpan<RealtimeSaturatedLadderStageUpdate> saturatedUpdates,
		Span<RealtimeSaturatedLadderCoefficients> saturatedScratch,
		ReadOnlySpan<RealtimeAllPassStageUpdate> allPassUpdates,
		Span<RealtimeAllPassCoefficient> allPassScratch)
		=> TryApplyRealtimeNumericUpdates(tptUpdates, tptScratch,
			biquadUpdates, biquadScratch, ladderUpdates, ladderScratch,
			saturatedUpdates, saturatedScratch, allPassUpdates, allPassScratch,
			ReadOnlySpan<RealtimeFeedbackCombStageUpdate>.Empty,
			Span<RealtimeFeedbackCombCoefficients>.Empty);

	// Keep the previous four-family call contract for existing internal users.
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch,
		ReadOnlySpan<RealtimeLinearLadderStageUpdate> ladderUpdates,
		Span<RealtimeLinearLadderCoefficients> ladderScratch,
		ReadOnlySpan<RealtimeSaturatedLadderStageUpdate> saturatedUpdates,
		Span<RealtimeSaturatedLadderCoefficients> saturatedScratch)
		=> TryApplyRealtimeNumericUpdates(tptUpdates, tptScratch,
			biquadUpdates, biquadScratch, ladderUpdates, ladderScratch,
			saturatedUpdates, saturatedScratch,
			ReadOnlySpan<RealtimeAllPassStageUpdate>.Empty,
			Span<RealtimeAllPassCoefficient>.Empty);

	// Retain the previous 3-family call contract.
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch,
		ReadOnlySpan<RealtimeLinearLadderStageUpdate> ladderUpdates,
		Span<RealtimeLinearLadderCoefficients> ladderScratch)
		=> TryApplyRealtimeNumericUpdates(tptUpdates, tptScratch,
			biquadUpdates, biquadScratch, ladderUpdates, ladderScratch,
			ReadOnlySpan<RealtimeSaturatedLadderStageUpdate>.Empty,
			Span<RealtimeSaturatedLadderCoefficients>.Empty);

	// Retain the previous TPT/Biquad overload for existing internal callers.
	internal bool TryApplyRealtimeNumericUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> tptUpdates,
		Span<RealtimeTptCoefficients> tptScratch,
		ReadOnlySpan<RealtimeBiquadStageUpdate> biquadUpdates,
		Span<RealtimeBiquadCoefficients> biquadScratch)
		=> TryApplyRealtimeNumericUpdates(tptUpdates, tptScratch,
			biquadUpdates, biquadScratch,
			ReadOnlySpan<RealtimeLinearLadderStageUpdate>.Empty,
			Span<RealtimeLinearLadderCoefficients>.Empty);

	internal bool TryApplyRealtimeTptUpdates(
		ReadOnlySpan<RealtimeTptStageUpdate> updates,
		Span<RealtimeTptCoefficients> scratch)
		=> TryApplyRealtimeNumericUpdates(updates, scratch,
			ReadOnlySpan<RealtimeBiquadStageUpdate>.Empty,
			Span<RealtimeBiquadCoefficients>.Empty);

	/// <summary>
	/// Scalar graph-signal path for a preconfigured mono invocation.
	/// Avoids constructing a temporary frame span for each FM graph sample.
	/// Shares the same chronological stage/history sequence as ProcessFrame.
	/// </summary>
	public float ProcessMonoFrame(float sample)
	{
		if (OutputChannelCount != 1)
			throw new InvalidOperationException(
				"Scalar processing requires exactly one output channel.");
		if (_inputFinished)
			throw new InvalidOperationException(
				"Cannot accept input after FinishInput; reset first.");
		foreach (RunningStage stage in _stages)
			sample = stage.Channels[0].Process(sample);
		return sample;
	}

	/// <summary>Advance exactly one output frame in place.</summary>
	public void ProcessFrame(Span<float> channels)
	{
		if (channels.Length != OutputChannelCount)
			throw new ArgumentException(
				"Frame width does not match output-channel count.", nameof(channels));
		if (_inputFinished)
			throw new InvalidOperationException(
				"Cannot accept input after FinishInput; reset first.");
		ProcessCore(channels);
	}

	/// <summary>Interleaved frames; the span length must be a multiple of
	/// output-channel count. Chunking does not alter the state trajectory.</summary>
	public void ProcessInterleaved(Span<float> samples)
	{
		if (samples.Length % OutputChannelCount != 0)
			throw new ArgumentException(
				"PCM span must contain whole output frames.", nameof(samples));
		if (_inputFinished)
			throw new InvalidOperationException(
				"Cannot accept input after FinishInput; reset first.");
		for (int offset = 0; offset < samples.Length; offset += OutputChannelCount)
			ProcessCore(samples.Slice(offset, OutputChannelCount));
	}

	/// <summary>
	/// Stop adding excitation and enable a strictly finite zero-input tail.
	/// Input sources decide when they are finished; the filter chain never
	/// invents extra notes, invokes audio callbacks or runs background work.
	/// </summary>
	public void FinishInput()
	{
		if (_inputFinished)
			return;
		_inputFinished = true;
		_tailRemainingFrames = _tailBudgetFrames;
	}

	/// <summary>
	/// Writes up to destination.Length / channelCount frames of the filtered
	/// zero-input tail. Any excess is cleared to exact zero. Returns the
	/// number of frames actually advanced through DSP, so callers can stop
	/// rendering as soon as the finite tail budget expires.
	/// </summary>
	public int RenderTail(Span<float> destination)
	{
		if (!_inputFinished)
			throw new InvalidOperationException("Call FinishInput before rendering tail.");
		if (destination.Length % OutputChannelCount != 0)
			throw new ArgumentException(
				"PCM span must contain whole output frames.", nameof(destination));
		destination.Clear();
		int frames = Math.Min(destination.Length / OutputChannelCount,
			_tailRemainingFrames);
		for (int frame = 0; frame < frames; frame++)
			ProcessCore(destination.Slice(
				frame * OutputChannelCount, OutputChannelCount));
		_tailRemainingFrames -= frames;
		return frames;
	}

	/// <summary>Discard all histories and restart chronological processing
	/// from frame zero. A seek reconstructs deterministically by reset and
	/// replay of earlier input frames, not by seeking mutable state blindly.</summary>
	public void Reset()
	{
		RunningStage[] clean = new RunningStage[_stages.Length];
		for (int i = 0; i < _stages.Length; i++)
		{
			RunningStage previous = _stages[i];
			IStreamingFilterStageState[] states =
				new IStreamingFilterStageState[OutputChannelCount];
			for (int channel = 0; channel < states.Length; channel++)
				states[channel] = previous.Plan.CreateState()
					?? throw new InvalidOperationException(
						"Filter plan returned a null channel state.");
			clean[i] = new RunningStage
			{
				Stage = previous.Stage,
				Plan = previous.Plan,
				Channels = states,
			};
		}
		_stages = clean;
		_inputFinished = false;
		_tailRemainingFrames = 0;
	}

	/// <summary>Deep-copy mutable DSP histories for deliberate voice forks,
	/// e.g. later NNA host migration. It does not share processor histories.</summary>
	public StreamingFilterChain Fork() => new(this);

	private void ProcessCore(Span<float> channels)
	{
		foreach (RunningStage stage in _stages)
			for (int channel = 0; channel < OutputChannelCount; channel++)
				channels[channel] = stage.Channels[channel].Process(channels[channel]);
	}
}
