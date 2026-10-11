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

	internal static long DebugBeforeFastAllocated;
	internal static long DebugPreRetuneAllocated;
	internal static long DebugRetuneCallAllocated;
	internal static long DebugPostRetuneAllocated;
	internal static long DebugChainAllocated;
	internal static long DebugFastSamples;
	internal static long DebugFastAllocated;
	internal static long DebugProcessAllocated;
		private readonly FilterInstance? _initial;
	private readonly Binding[] _bindings;
	private readonly FastStage[] _fastStages;
	private readonly bool _useFastTpt;
	private readonly int _audioIndex;

	// A single prepared TPT stage may have both cutoff and Q connected.
	// Indices address the existing ordered binding array, or -1 for an
	// unconnected parameter that retains its independently authored default.
	private readonly record struct FastStage(
		int StageIndex, int CutoffBindingIndex, int QBindingIndex,
		double BaseCutoff, double BaseQ);


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

		List<FastStage> fast = [];
		bool supported = bindings.Count != 0;
		ConfiguredFilterStage[] stages = _initial?.GetStages().ToArray() ?? [];
		for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
		{
			ConfiguredFilterStage stage = stages[stageIndex];
			int cutoff = -1;
			int q = -1;
			bool stageBound = false;
			for (int bindingIndex = 0; bindingIndex < _bindings.Length; bindingIndex++)
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
					&& SvfFilterStageFactory.IsSvf(stage.Kind))
					q = bindingIndex;
				else
					supported = false;
			}
			if (!stageBound)
				continue;
			if (stage.Kind != FilterKind.OnePole
				&& !SvfFilterStageFactory.IsSvf(stage.Kind))
				supported = false;
			if (supported)
				fast.Add(new FastStage(stageIndex, cutoff, q,
					stage.Parameters.GetParameter("CutoffHz").NumericValue,
					q >= 0 || SvfFilterStageFactory.IsSvf(stage.Kind)
						? stage.Parameters.GetParameter("Q").NumericValue : 1));
		}
		_useFastTpt = supported && fast.Count > 0;
		_fastStages = _useFastTpt ? [.. fast] : [];
	}

	public sealed class State
	{
		internal State(FilterInstance? initial,
			double[] initialValues, int sampleRate, int fastCount)
		{
			Configuration = initial?.Clone();
			Values = (double[])initialValues.Clone();
			FastUpdates = new RealtimeTptStageUpdate[fastCount];
			FastScratch = new RealtimeTptCoefficients[fastCount];
			if (Configuration is not null)
				Chain = new StreamingFilterChain(
					new FilterChain([Configuration]), sampleRate, 1);
		}

		internal FilterInstance? Configuration { get; set; }
		internal StreamingFilterChain? Chain { get; }
		internal double[] Values { get; }
		internal RealtimeTptStageUpdate[] FastUpdates { get; }
		internal RealtimeTptCoefficients[] FastScratch { get; }
	}

	public State CreateState(int sampleRate)
		=> new(_initial,
			_bindings.Select(item => item.InitialValue).ToArray(),
			sampleRate, _fastStages.Length);

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
		long debugProcessStart = GC.GetAllocatedBytesForCurrentThread();
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

		long debugBeforeRetune = GC.GetAllocatedBytesForCurrentThread();
		DebugPreRetuneAllocated += debugBeforeRetune - debugProcessStart;
		if (_bindings.Length != 0 && !stopExcitation)
			Retune(state, nodeValues);
		long debugAfterRetune = GC.GetAllocatedBytesForCurrentThread();
		DebugRetuneCallAllocated += debugAfterRetune - debugBeforeRetune;

		long debugChainStart = GC.GetAllocatedBytesForCurrentThread();
		DebugPostRetuneAllocated += debugChainStart - debugAfterRetune;
		float sample = state.Chain.ProcessMonoFrame(input);
		DebugChainAllocated += GC.GetAllocatedBytesForCurrentThread() - debugChainStart;
		DebugProcessAllocated += GC.GetAllocatedBytesForCurrentThread() - debugProcessStart;
		return sample;
	}

	private void Retune(State state, double[] sourceValues)
	{
		long debugBeforeFastStart = GC.GetAllocatedBytesForCurrentThread();
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

		if (_useFastTpt)
		{
			DebugBeforeFastAllocated += GC.GetAllocatedBytesForCurrentThread() - debugBeforeFastStart;
			long debugFastStart = GC.GetAllocatedBytesForCurrentThread();
			DebugFastSamples++;
			// All frame targets are computed into invocation-owned scratch
			// before the streaming chain preflights every selected stage.
			// No LINQ, DSP plan allocation, FilterInstance cloning or
			// dictionary construction occurs in this successful path.
			for (int i = 0; i < _fastStages.Length; i++)
			{
				FastStage stage = _fastStages[i];
				double cutoff = stage.CutoffBindingIndex < 0
					? stage.BaseCutoff
					: sourceValues[_bindings[stage.CutoffBindingIndex].ValueIndex];
				double q = stage.QBindingIndex < 0
					? stage.BaseQ
					: sourceValues[_bindings[stage.QBindingIndex].ValueIndex];
				state.FastUpdates[i] =
					new RealtimeTptStageUpdate(stage.StageIndex, cutoff, q);
			}
			if (!state.Chain!.TryApplyRealtimeTptUpdates(
				state.FastUpdates, state.FastScratch))
				return;
			for (int i = 0; i < _bindings.Length; i++)
				state.Values[i] = sourceValues[_bindings[i].ValueIndex];
			DebugFastAllocated += GC.GetAllocatedBytesForCurrentThread() - debugFastStart;
			return;
		}

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
