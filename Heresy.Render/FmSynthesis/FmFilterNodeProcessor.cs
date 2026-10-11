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
				else
					supported = false;
			}
			if (!stageBound)
				continue;
			double baseCutoff = stage.Parameters
				.GetParameter("CutoffHz").NumericValue;
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
			else
				supported = false;
		}
		_useFastNumeric = supported && tpt.Count + biquads.Count > 0;
		_fastStages = _useFastNumeric ? [.. tpt] : [];
		_fastBiquads = _useFastNumeric ? [.. biquads] : [];

	}

	public sealed class State
	{
		internal State(FilterInstance? initial,
			double[] initialValues, int sampleRate, int tptCount, int biquadCount)
		{
			Configuration = initial?.Clone();
			Values = (double[])initialValues.Clone();
			FastUpdates = new RealtimeTptStageUpdate[tptCount];
			FastScratch = new RealtimeTptCoefficients[tptCount];
			BiquadUpdates = new RealtimeBiquadStageUpdate[biquadCount];
			BiquadScratch = new RealtimeBiquadCoefficients[biquadCount];
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
	}

	public State CreateState(int sampleRate)
		=> new(_initial,
			_bindings.Select(item => item.InitialValue).ToArray(),
			sampleRate, _fastStages.Length, _fastBiquads.Length);

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

		if (!state.Chain!.TryApplyRealtimeNumericUpdates(
			state.FastUpdates.AsSpan(0, tptCount), state.FastScratch,
			state.BiquadUpdates.AsSpan(0, biquadCount), state.BiquadScratch))
			return;
		for (int i = 0; i < _bindings.Length; i++)
			state.Values[i] = sourceValues[_bindings[i].ValueIndex];
	}

	/// <summary>
	/// Full atomic preparation remains the validated compatibility path
	/// for filter families without a dedicated numeric update kernel.
	/// Its closure allocations are isolated from the TPT realtime method.
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
