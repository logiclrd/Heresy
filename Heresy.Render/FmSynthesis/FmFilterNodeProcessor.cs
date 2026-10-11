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
	private readonly int _audioIndex;

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
	}

	public sealed class State
	{
		internal State(FilterInstance? initial, int bindingCount,
			double[] initialValues, int sampleRate)
		{
			Configuration = initial?.Clone();
			Values = (double[])initialValues.Clone();
			if (Configuration is not null)
				Chain = new StreamingFilterChain(
					new FilterChain([Configuration]), sampleRate, 1);
		}

		internal FilterInstance? Configuration { get; set; }
		internal StreamingFilterChain? Chain { get; }
		internal double[] Values { get; }
	}

	public State CreateState(int sampleRate)
		=> new(_initial, _bindings.Length,
			_bindings.Select(item => item.InitialValue).ToArray(), sampleRate);

	/// <summary>
	/// Evaluate all connected parameters as absolute values in the authored
	/// descriptor's units. The entire proposed set is validated/prepared
	/// atomically. Invalid or nonfinite modulation leaves the previously
	/// committed coefficients and histories untouched.
	/// </summary>
	public float Process(State state, IReadOnlyList<double> nodeValues,
		bool stopExcitation)
	{
		ArgumentNullException.ThrowIfNull(state);
		float input = 0;
		if (!stopExcitation && _audioIndex >= 0)
		{
			double audio = nodeValues[_audioIndex];
			if (double.IsFinite(audio))
				input = (float)Math.Clamp(audio, -float.MaxValue, float.MaxValue);
		}

		if (state.Chain is null || state.Configuration is null)
			return input;

		if (_bindings.Length != 0 && !stopExcitation)
			Retune(state, nodeValues);

		Span<float> sample = stackalloc float[1];
		sample[0] = input;
		state.Chain.ProcessFrame(sample);
		return sample[0];
	}

	private void Retune(State state, IReadOnlyList<double> sourceValues)
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
