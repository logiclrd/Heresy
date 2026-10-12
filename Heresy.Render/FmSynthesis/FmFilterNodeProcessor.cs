using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Render.Filters;

namespace Heresy.Render.FmSynthesis;

/// <summary>
/// Immutable compiled port routing for one FM Filter node. Each invocation
/// owns its own mutable configuration and DSP histories; there is no shared
/// ring or coefficient state between notes.
/// </summary>
internal sealed class FmFilterNodeProcessor
{
	private sealed record Binding(int StageId, string ParameterName,
		int ValueIndex, double InitialValue);

	private readonly FilterInstance? _initial;
	private readonly Binding[] _bindings;
	private readonly FastStage[] _fastStages;
	private readonly FastBiquadStage[] _fastBiquads;
	private readonly FastLinearLadderStage[] _fastLadders;
	private readonly FastSaturatedLadderStage[] _fastSaturatedLadders;
	private readonly FastAllPassStage[] _fastAllPasses;
	private readonly FastFeedbackCombStage[] _fastFeedbackCombs;
	private readonly FastFeedforwardCombStage[] _fastFeedforwardCombs;
	private readonly FastFirControlStage[] _fastFirs;
	private readonly bool _useFastNumeric;
	private readonly int _audioIndex;

	// Each stage stores stable indices into the connected numeric binding
	// array. A missing input inherits that parameter's authored value.
	private readonly record struct FastStage(
		int StageIndex, int CutoffBindingIndex, int QBindingIndex,
		double BaseCutoff, double BaseQ);

	private readonly record struct FastBiquadStage(
		int StageIndex, int CutoffBindingIndex, int QBindingIndex,
		int SlopeBindingIndex, int GainBindingIndex,
		double BaseCutoff, double BaseQ, double BaseSlope, double BaseGain);

	private readonly record struct FastLinearLadderStage(
		int StageIndex, int CutoffBindingIndex, int ResonanceBindingIndex,
		double BaseCutoff, double BaseResonance);

	private readonly record struct FastSaturatedLadderStage(
		int StageIndex, int CutoffBindingIndex, int ResonanceBindingIndex,
		int DriveBindingIndex, double BaseCutoff, double BaseResonance,
		double BaseDrive);

	private readonly record struct FastAllPassStage(
		int StageIndex, int CutoffBindingIndex, double BaseCutoff);

	private readonly record struct FastFeedbackCombStage(
		int StageIndex, int FeedbackBindingIndex, int MixBindingIndex,
		double BaseFeedback, double BaseMix);

	private readonly record struct FastFeedforwardCombStage(
		int StageIndex, int FeedforwardBindingIndex, int MixBindingIndex,
		double BaseFeedforward, double BaseMix);

	private readonly record struct FastFirControlStage(
		int StageIndex, int StageId, FilterKind Kind,
		int CutoffBindingIndex, int LowBindingIndex, int HighBindingIndex,
		double BaseCutoff, double BaseLow, double BaseHigh,
		int TapCount, double WidthHz, string PhaseResponse);

	public FmFilterNodeProcessor(FmFilterNode node,
		IReadOnlyDictionary<int, int> indexById)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(indexById);
		_initial = node.Configuration?.Clone();
		_audioIndex = node.AudioInputNodeId is int audioId
			? indexById[audioId] : -1;
		List<Binding> bindings = [];
		foreach (FmFilterInputPort port in node.GetPorts())
		{
			if (port.IsAudio || port.SourceNodeId is not int sourceId)
				continue;
			if (_initial is null)
				continue;
			double initial = _initial.GetStageParameters(port.StageId)
				.GetParameter(port.ParameterName).NumericValue;
			bindings.Add(new Binding(port.StageId, port.ParameterName,
				indexById[sourceId], initial));
		}
		_bindings = [.. bindings];

		List<FastStage> tpt = [];
		List<FastBiquadStage> biquads = [];
		List<FastLinearLadderStage> ladders = [];
		List<FastSaturatedLadderStage> saturatedLadders = [];
		List<FastAllPassStage> allPasses = [];
		List<FastFeedbackCombStage> feedbackCombs = [];
		List<FastFeedforwardCombStage> feedforwardCombs = [];
		List<FastFirControlStage> firs = [];
		bool supported = _bindings.Length != 0;
		ConfiguredFilterStage[] stages = _initial?.GetStages().ToArray() ?? [];
		for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
		{
			ConfiguredFilterStage stage = stages[stageIndex];
			bool isSvf = SvfFilterStageFactory.IsSvf(stage.Kind);
			bool isBiquad = BiquadFilterStageFactory.IsBiquad(stage.Kind);
			bool isShelf = stage.Kind is FilterKind.BiquadLowShelf
				or FilterKind.BiquadHighShelf;
			int cutoff = -1;
			int q = -1;
			int slope = -1;
			int gain = -1;
			int resonance = -1;
			int drive = -1;
			int feedback = -1;
			int feedforward = -1;
			int mix = -1;
			int lowCutoff = -1;
			int highCutoff = -1;
			bool stageBound = false;
			for (int bindingIndex = 0; bindingIndex < _bindings.Length;
				bindingIndex++)
			{
				Binding binding = _bindings[bindingIndex];
				if (binding.StageId != stage.StageId)
					continue;
				stageBound = true;
				if (string.Equals(binding.ParameterName, "CutoffHz",
					StringComparison.OrdinalIgnoreCase))
					cutoff = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Q",
					StringComparison.OrdinalIgnoreCase)
					&& (isSvf || (isBiquad && !isShelf)))
					q = bindingIndex;
				else if (string.Equals(binding.ParameterName, "ShelfSlope",
					StringComparison.OrdinalIgnoreCase) && isShelf)
					slope = bindingIndex;
				else if (string.Equals(binding.ParameterName, "GainDb",
					StringComparison.OrdinalIgnoreCase)
					&& (isShelf || stage.Kind == FilterKind.BiquadPeaking))
					gain = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Resonance",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind is (FilterKind.LadderLinear
						or FilterKind.LadderSaturated))
					resonance = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Drive",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind == FilterKind.LadderSaturated)
					drive = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Feedback",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind == FilterKind.CombFeedback)
					feedback = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Feedforward",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind == FilterKind.CombFeedforward)
					feedforward = bindingIndex;
				else if (string.Equals(binding.ParameterName, "Mix",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind is (FilterKind.CombFeedback or FilterKind.CombFeedforward))
					mix = bindingIndex;
				else if (string.Equals(binding.ParameterName, "LowCutoffHz",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind is (FilterKind.FirBandPass or FilterKind.FirNotch))
					lowCutoff = bindingIndex;
				else if (string.Equals(binding.ParameterName, "HighCutoffHz",
					StringComparison.OrdinalIgnoreCase)
					&& stage.Kind is (FilterKind.FirBandPass or FilterKind.FirNotch))
					highCutoff = bindingIndex;
				else
					supported = false;
			}
			if (!stageBound)
				continue;
			// Both Comb kinds have no CutoffHz: only read the
			// descriptor for families that actually expose it.
			double baseCutoff = stage.Kind is (FilterKind.CombFeedback
				or FilterKind.CombFeedforward or FilterKind.FirBandPass
				or FilterKind.FirNotch)
				? 0 : stage.Parameters.GetParameter("CutoffHz").NumericValue;
			if (stage.Kind == FilterKind.OnePole || isSvf)
			{
				tpt.Add(new FastStage(stageIndex, cutoff, q, baseCutoff,
					isSvf ? stage.Parameters.GetParameter("Q").NumericValue : 1));
			}
			else if (isBiquad)
			{
				biquads.Add(new FastBiquadStage(stageIndex,
					cutoff, q, slope, gain, baseCutoff,
					isShelf ? 1 : stage.Parameters.GetParameter("Q").NumericValue,
					isShelf ? stage.Parameters.GetParameter("ShelfSlope").NumericValue
						: 1,
					isShelf || stage.Kind == FilterKind.BiquadPeaking
						? stage.Parameters.GetParameter("GainDb").NumericValue : 0));
			}
			else if (stage.Kind == FilterKind.LadderLinear)
			{
				ladders.Add(new FastLinearLadderStage(
					stageIndex, cutoff, resonance, baseCutoff,
					stage.Parameters.GetParameter("Resonance").NumericValue));
			}
			else if (stage.Kind == FilterKind.LadderSaturated)
			{
				saturatedLadders.Add(new FastSaturatedLadderStage(
					stageIndex, cutoff, resonance, drive, baseCutoff,
					stage.Parameters.GetParameter("Resonance").NumericValue,
					stage.Parameters.GetParameter("Drive").NumericValue));
			}
			else if (stage.Kind == FilterKind.AllPass)
				allPasses.Add(new FastAllPassStage(stageIndex, cutoff, baseCutoff));
			else if (stage.Kind == FilterKind.CombFeedback)
				feedbackCombs.Add(new FastFeedbackCombStage(
					stageIndex, feedback, mix,
					stage.Parameters.GetParameter("Feedback").NumericValue,
					stage.Parameters.GetParameter("Mix").NumericValue));
			else if (stage.Kind == FilterKind.CombFeedforward)
				feedforwardCombs.Add(new FastFeedforwardCombStage(
					stageIndex, feedforward, mix,
					stage.Parameters.GetParameter("Feedforward").NumericValue,
					stage.Parameters.GetParameter("Mix").NumericValue));
			else if (LinearPhaseFirFilterStageFactory.IsStandardFir(stage.Kind))
			{
				bool band = stage.Kind is FilterKind.FirBandPass or FilterKind.FirNotch;
				firs.Add(new FastFirControlStage(stageIndex, stage.StageId,
					stage.Kind, cutoff, lowCutoff, highCutoff, baseCutoff,
					band ? stage.Parameters.GetParameter("LowCutoffHz").NumericValue : 0,
					band ? stage.Parameters.GetParameter("HighCutoffHz").NumericValue : 0,
					(int)stage.Parameters.GetParameter("TapCount").NumericValue,
					stage.Parameters.GetParameter("TransitionWidthHz").NumericValue,
					stage.Parameters.GetParameter("PhaseResponse").ChoiceValue!));
			}
			else
				supported = false;
		}
		_useFastNumeric = supported
			&& tpt.Count + biquads.Count + ladders.Count
				+ saturatedLadders.Count + allPasses.Count
				+ feedbackCombs.Count + feedforwardCombs.Count
				+ firs.Count > 0;
		_fastStages = _useFastNumeric ? [.. tpt] : [];
		_fastBiquads = _useFastNumeric ? [.. biquads] : [];
		_fastLadders = _useFastNumeric ? [.. ladders] : [];
		_fastSaturatedLadders = _useFastNumeric ? [.. saturatedLadders] : [];
		_fastAllPasses = _useFastNumeric ? [.. allPasses] : [];
		_fastFeedbackCombs = _useFastNumeric ? [.. feedbackCombs] : [];
		_fastFeedforwardCombs = _useFastNumeric ? [.. feedforwardCombs] : [];
		_fastFirs = _useFastNumeric ? [.. firs] : [];

	}

	public sealed class State
	{
		internal State(FilterInstance? initial,
			double[] initialValues, int sampleRate, int tptCount, int biquadCount, int ladderCount,
			int saturatedLadderCount, int allPassCount, int combCount,
			int feedforwardCount, int firCount)
		{
			Configuration = initial?.Clone();
			Values = (double[])initialValues.Clone();
			FastUpdates = new RealtimeTptStageUpdate[tptCount];
			FastScratch = new RealtimeTptCoefficients[tptCount];
			BiquadUpdates = new RealtimeBiquadStageUpdate[biquadCount];
			BiquadScratch = new RealtimeBiquadCoefficients[biquadCount];
			LadderUpdates = new RealtimeLinearLadderStageUpdate[ladderCount];
			LadderScratch = new RealtimeLinearLadderCoefficients[ladderCount];
			SaturatedUpdates = new RealtimeSaturatedLadderStageUpdate[saturatedLadderCount];
			SaturatedScratch = new RealtimeSaturatedLadderCoefficients[saturatedLadderCount];
			AllPassUpdates = new RealtimeAllPassStageUpdate[allPassCount];
			AllPassScratch = new RealtimeAllPassCoefficient[allPassCount];
			CombUpdates = new RealtimeFeedbackCombStageUpdate[combCount];
			CombScratch = new RealtimeFeedbackCombCoefficients[combCount];
			FeedforwardUpdates = new RealtimeFeedforwardCombStageUpdate[feedforwardCount];
			FeedforwardScratch = new RealtimeFeedforwardCombCoefficients[feedforwardCount];
			if (firCount > 0)
			{
				FirControl = new FirControlPreparationPolicy(sampleRate);
				FirRequests = new FirControlRequest[
					Math.Min(firCount, FirControlPreparationPolicy.MaximumQueuedStages)];
			}
			if (Configuration is not null)
				Chain = new StreamingFilterChain(
					new FilterChain([Configuration]), sampleRate, 1);
		}

		internal FilterInstance? Configuration { get; set; }
		internal StreamingFilterChain? Chain { get; }
		internal double[] Values { get; }
		internal RealtimeTptStageUpdate[] FastUpdates { get; }
		internal RealtimeTptCoefficients[] FastScratch { get; }
		internal RealtimeBiquadStageUpdate[] BiquadUpdates { get; }
		internal RealtimeBiquadCoefficients[] BiquadScratch { get; }
		internal RealtimeLinearLadderStageUpdate[] LadderUpdates { get; }
		internal RealtimeLinearLadderCoefficients[] LadderScratch { get; }
		internal RealtimeSaturatedLadderStageUpdate[] SaturatedUpdates { get; }
		internal RealtimeSaturatedLadderCoefficients[] SaturatedScratch { get; }
		internal RealtimeAllPassStageUpdate[] AllPassUpdates { get; }
		internal RealtimeAllPassCoefficient[] AllPassScratch { get; }
		internal RealtimeFeedbackCombStageUpdate[] CombUpdates { get; }
		internal RealtimeFeedbackCombCoefficients[] CombScratch { get; }
		internal RealtimeFeedforwardCombStageUpdate[] FeedforwardUpdates { get; }
		internal RealtimeFeedforwardCombCoefficients[] FeedforwardScratch { get; }
		internal FirControlPreparationPolicy? FirControl { get; }
		internal FirControlRequest[]? FirRequests { get; }
	}

	public State CreateState(int sampleRate)
		=> new(_initial,
			_bindings.Select(item => item.InitialValue).ToArray(),
			sampleRate, _fastStages.Length, _fastBiquads.Length,
			_fastLadders.Length, _fastSaturatedLadders.Length,
			_fastAllPasses.Length, _fastFeedbackCombs.Length,
			_fastFeedforwardCombs.Length, _fastFirs.Length);

	/// <summary>
	/// Evaluate all connected parameters as absolute values in the authored
	/// descriptor's units. The entire proposed set is validated/prepared
	/// atomically. Invalid or nonfinite modulation leaves the previously
	/// committed coefficients and histories untouched.
	/// </summary>
	public float Process(State state, double[] nodeValues,
		bool stopExcitation)
	{
		ArgumentNullException.ThrowIfNull(state);
		float input = 0;
		// Upstream oscillators/envelopes/constants are already silenced
		// by the FM graph at its natural end. A preceding Filter is NOT
		// silenced: its residual output must flow into subsequent filters.
		if (_audioIndex >= 0)
		{
			double audio = nodeValues[_audioIndex];
			if (double.IsFinite(audio))
				input = (float)Math.Clamp(audio, -float.MaxValue, float.MaxValue);
		}

		if (state.Chain is null || state.Configuration is null)
			return input;

		if (_bindings.Length != 0 && !stopExcitation)
			Retune(state, nodeValues);

		return state.Chain.ProcessMonoFrame(input);
	}

	private static bool HasChanged(int bindingIndex, State state,
		double[] values, Binding[] bindings)
		=> bindingIndex >= 0
			&& values[bindings[bindingIndex].ValueIndex]
				!= state.Values[bindingIndex];

	private static double ValueOrDefault(int bindingIndex, double baseValue,
		double[] values, Binding[] bindings)
		=> bindingIndex < 0
			? baseValue : values[bindings[bindingIndex].ValueIndex];

	private void Retune(State state, double[] sourceValues)
	{
		bool dirty = false;
		for (int i = 0; i < _bindings.Length; i++)
		{
			double value = sourceValues[_bindings[i].ValueIndex];
			if (!double.IsFinite(value))
				return;
			if (value != state.Values[i])
				dirty = true;
		}
		if (!dirty)
			return;

		if (!_useFastNumeric)
		{
			// The closure-producing LINQ compatibility code deliberately
			// lives in another method. Keeping it here allocated a
			// display-class even on the supposedly allocation-free branch.
			RetuneCompatibility(state, sourceValues);
			return;
		}

		// FIR parameter snapshots are only captured and admitted here:
		// no windowed-sinc design, phase conversion, FilterInstance clone
		// or filter history rebuild may happen in this live PCM method.
		// Until the per-phase FIR consumers are installed, any FIR change
		// keeps the previous valid kernel and prevents *all* mixed-family
		// updates in that frame.
		bool firDirty = false;
		foreach (FastFirControlStage stage in _fastFirs)
		{
			if (HasChanged(stage.CutoffBindingIndex, state, sourceValues, _bindings)
				|| HasChanged(stage.LowBindingIndex, state, sourceValues, _bindings)
				|| HasChanged(stage.HighBindingIndex, state, sourceValues, _bindings))
			{
				firDirty = true;
				break;
			}
		}
		if (firDirty)
		{
			FirControlRequest[]? requests = state.FirRequests;
			if (requests is null || _fastFirs.Length > requests.Length)
			{
				state.FirControl?.Clear();
				return;
			}
			for (int i = 0; i < _fastFirs.Length; i++)
			{
				FastFirControlStage stage = _fastFirs[i];
				requests[i] = new FirControlRequest(
					stage.StageIndex, stage.StageId, stage.Kind,
					ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
						sourceValues, _bindings),
					ValueOrDefault(stage.LowBindingIndex, stage.BaseLow,
						sourceValues, _bindings),
					ValueOrDefault(stage.HighBindingIndex, stage.BaseHigh,
						sourceValues, _bindings),
					stage.TapCount, stage.WidthHz, stage.PhaseResponse);
			}
			state.FirControl!.TryOffer(requests.AsSpan(0, _fastFirs.Length));
			return;
		}
		// The source returned to its accepted numeric FIR settings:
		// discard obsolete pending requests before handling other stages.
		state.FirControl?.Clear();

		int tptCount = 0;
		foreach (FastStage stage in _fastStages)
		{
			if (!HasChanged(stage.CutoffBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.QBindingIndex, state, sourceValues, _bindings))
				continue;
			state.FastUpdates[tptCount++] = new RealtimeTptStageUpdate(
				stage.StageIndex,
				ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
					sourceValues, _bindings),
				ValueOrDefault(stage.QBindingIndex, stage.BaseQ,
					sourceValues, _bindings));
		}

		int biquadCount = 0;
		foreach (FastBiquadStage stage in _fastBiquads)
		{
			if (!HasChanged(stage.CutoffBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.QBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.SlopeBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.GainBindingIndex, state, sourceValues, _bindings))
				continue;
			state.BiquadUpdates[biquadCount++] = new RealtimeBiquadStageUpdate(
				stage.StageIndex,
				ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
					sourceValues, _bindings),
				ValueOrDefault(stage.QBindingIndex, stage.BaseQ,
					sourceValues, _bindings),
				ValueOrDefault(stage.SlopeBindingIndex, stage.BaseSlope,
					sourceValues, _bindings),
				ValueOrDefault(stage.GainBindingIndex, stage.BaseGain,
					sourceValues, _bindings));
		}

		int ladderCount = 0;
		foreach (FastLinearLadderStage stage in _fastLadders)
		{
			if (!HasChanged(stage.CutoffBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.ResonanceBindingIndex,
					state, sourceValues, _bindings))
				continue;
			state.LadderUpdates[ladderCount++] = new RealtimeLinearLadderStageUpdate(
				stage.StageIndex,
				ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
					sourceValues, _bindings),
				ValueOrDefault(stage.ResonanceBindingIndex, stage.BaseResonance,
					sourceValues, _bindings));
		}

		int saturatedCount = 0;
		foreach (FastSaturatedLadderStage stage in _fastSaturatedLadders)
		{
			if (!HasChanged(stage.CutoffBindingIndex, state, sourceValues, _bindings)
				&& !HasChanged(stage.ResonanceBindingIndex,
					state, sourceValues, _bindings)
				&& !HasChanged(stage.DriveBindingIndex,
					state, sourceValues, _bindings))
				continue;
			state.SaturatedUpdates[saturatedCount++] =
				new RealtimeSaturatedLadderStageUpdate(
					stage.StageIndex,
					ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
						sourceValues, _bindings),
					ValueOrDefault(stage.ResonanceBindingIndex, stage.BaseResonance,
						sourceValues, _bindings),
					ValueOrDefault(stage.DriveBindingIndex, stage.BaseDrive,
						sourceValues, _bindings));
		}

		int allPassCount = 0;
		foreach (FastAllPassStage stage in _fastAllPasses)
		{
			if (!HasChanged(stage.CutoffBindingIndex,
				state, sourceValues, _bindings))
				continue;
			state.AllPassUpdates[allPassCount++] = new RealtimeAllPassStageUpdate(
				stage.StageIndex,
				ValueOrDefault(stage.CutoffBindingIndex, stage.BaseCutoff,
					sourceValues, _bindings));
		}

		int combCount = 0;
		foreach (FastFeedbackCombStage stage in _fastFeedbackCombs)
		{
			if (!HasChanged(stage.FeedbackBindingIndex,
				state, sourceValues, _bindings)
				&& !HasChanged(stage.MixBindingIndex,
					state, sourceValues, _bindings))
				continue;
			state.CombUpdates[combCount++] = new RealtimeFeedbackCombStageUpdate(
				stage.StageIndex,
				ValueOrDefault(stage.FeedbackBindingIndex, stage.BaseFeedback,
					sourceValues, _bindings),
				ValueOrDefault(stage.MixBindingIndex, stage.BaseMix,
					sourceValues, _bindings));
		}

		int feedforwardCount = 0;
		foreach (FastFeedforwardCombStage stage in _fastFeedforwardCombs)
		{
			if (!HasChanged(stage.FeedforwardBindingIndex,
				state, sourceValues, _bindings)
				&& !HasChanged(stage.MixBindingIndex,
					state, sourceValues, _bindings))
				continue;
			state.FeedforwardUpdates[feedforwardCount++] =
				new RealtimeFeedforwardCombStageUpdate(
					stage.StageIndex,
					ValueOrDefault(stage.FeedforwardBindingIndex,
						stage.BaseFeedforward, sourceValues, _bindings),
					ValueOrDefault(stage.MixBindingIndex, stage.BaseMix,
						sourceValues, _bindings));
		}

		if (!state.Chain!.TryApplyRealtimeNumericUpdates(
			state.FastUpdates.AsSpan(0, tptCount), state.FastScratch,
			state.BiquadUpdates.AsSpan(0, biquadCount), state.BiquadScratch,
			state.LadderUpdates.AsSpan(0, ladderCount), state.LadderScratch,
			state.SaturatedUpdates.AsSpan(0, saturatedCount),
			state.SaturatedScratch,
			state.AllPassUpdates.AsSpan(0, allPassCount), state.AllPassScratch,
			state.CombUpdates.AsSpan(0, combCount), state.CombScratch,
			state.FeedforwardUpdates.AsSpan(0, feedforwardCount),
			state.FeedforwardScratch))
			return;
		for (int i = 0; i < _bindings.Length; i++)
			state.Values[i] = sourceValues[_bindings[i].ValueIndex];
	}

	/// <summary>
	/// Full atomic preparation remains the validated compatibility path
	/// for filter families without a dedicated numeric update kernel.
	/// Its closure allocations are isolated from the numeric realtime method.
	/// </summary>
	private void RetuneCompatibility(State state, double[] sourceValues)
	{
		// A full stage batch is essential for coupled constraints, e.g.
		// LowCutoffHz/HighCutoffHz crossing during an FM modulation frame.
		// The chain prepares the whole candidate before publishing it.
		FilterInstance candidate = state.Configuration!.Clone();
		try
		{
			foreach (IGrouping<int, (Binding Binding, int Index)> group
				in _bindings.Select((binding, index) => (Binding: binding, Index: index))
					.GroupBy(item => item.Binding.StageId))
			{
				candidate.SetStageParameters(group.Key, group.Select(item =>
					new KeyValuePair<string, FilterParameterValue>(
						item.Binding.ParameterName,
						FilterParameterValue.FromNumber(
							sourceValues[item.Binding.ValueIndex]))));
			}
			state.Chain!.Configure(new FilterChain([candidate]));
		}
		catch (Exception error) when (error is ArgumentException
			or InvalidOperationException or OverflowException)
		{
			// An invalid sample is not permitted to poison the running
			// filter. Resume at its last validated setting on next frame.
			return;
		}
		state.Configuration = candidate;
		for (int i = 0; i < _bindings.Length; i++)
			state.Values[i] = sourceValues[_bindings[i].ValueIndex];
	}
}
