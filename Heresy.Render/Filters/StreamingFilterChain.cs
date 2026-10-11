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
		_factory = factory ?? BypassFilterStageFactory.Instance;
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
		foreach (RunningStage item in prepared)
			requiredTail = Math.Min((long)MaximumTailFrames,
				requiredTail + item.Plan.TailFrames);
		_stages = prepared.ToArray(); // publish only after all work succeeded
		_tailBudgetFrames = (int)requiredTail;
		_tailRemainingFrames = 0;
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
