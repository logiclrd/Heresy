using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

namespace Heresy.Render.Filters;

/// <summary>
/// Immutable effective authoring parameters captured before a streaming
/// invocation begins. LayerIndex/StageIndex describe serial topology; StageId
/// and DefinitionId identify the authoring stage independent of display names.
/// </summary>
public sealed class PreparedFilterStage
{
	private readonly ReadOnlyDictionary<string, FilterParameterValue> _values;
	private readonly ReadOnlyDictionary<string, FilterParameterValue> _structural;

	public PreparedFilterStage(
		int layerIndex, int stageIndex, ObjectId definitionId, int stageId,
		FilterKind kind, long generation,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> parameters)
	{
		if (layerIndex < 0) throw new ArgumentOutOfRangeException(nameof(layerIndex));
		if (stageIndex < 0) throw new ArgumentOutOfRangeException(nameof(stageIndex));
		if (definitionId.IsNone) throw new ArgumentException(
			"Filter definition ID must be nonzero.", nameof(definitionId));
		if (stageId <= 0) throw new ArgumentOutOfRangeException(nameof(stageId));
		if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
		ArgumentNullException.ThrowIfNull(parameters);
		FilterSpecificationInstance validated = new(kind);
		KeyValuePair<string, FilterParameterValue>[] copy = parameters.ToArray();
		validated.SetParameters(copy);
		Dictionary<string, FilterParameterValue> values =
			new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, FilterParameterValue> structural =
			new(StringComparer.OrdinalIgnoreCase);
		foreach (FilterParameterDescriptor descriptor in validated.Parameters)
		{
			FilterParameterValue value = validated.GetParameter(descriptor.Name);
			values.Add(descriptor.Name, value);
			if (descriptor.IsStructural)
				structural.Add(descriptor.Name, value);
		}
		LayerIndex = layerIndex;
		StageIndex = stageIndex;
		DefinitionId = definitionId;
		StageId = stageId;
		Kind = kind;
		Generation = generation;
		_values = new(values);
		_structural = new(structural);
	}

	public int LayerIndex { get; }
	public int StageIndex { get; }
	public ObjectId DefinitionId { get; }
	public int StageId { get; }
	public FilterKind Kind { get; }
	public long Generation { get; }
	public IReadOnlyDictionary<string, FilterParameterValue> Parameters => _values;

	/// <summary>A history can be kept only if the stage's identity, serial
	/// topology, type generation, and all structural values still match.
	/// Changes to ordinary numeric parameters retain compatible histories.</summary>
	public bool IsHistoryCompatibleWith(PreparedFilterStage previous)
		=> previous is not null
			&& LayerIndex == previous.LayerIndex
			&& StageIndex == previous.StageIndex
			&& DefinitionId == previous.DefinitionId
			&& StageId == previous.StageId
			&& Kind == previous.Kind
			&& Generation == previous.Generation
			&& _structural.Count == previous._structural.Count
			&& _structural.All(pair => previous._structural.TryGetValue(
				pair.Key, out FilterParameterValue? value) && value == pair.Value);

	public bool HasSameParameters(PreparedFilterStage other)
		=> IsHistoryCompatibleWith(other)
			&& _values.Count == other._values.Count
			&& _values.All(pair => other._values.TryGetValue(
				pair.Key, out FilterParameterValue? value) && value == pair.Value);
}

/// <summary>
/// Configures a new kind of DSP processor outside the audio callback.
/// Preparation validates sample-rate-dependent constraints and precomputes
/// coefficients. Plans may be reused across output channels but must be
/// immutable and must not contain per-channel sample histories.
/// </summary>
public interface IStreamingFilterStageFactory
{
	IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate);
}

/// <summary>
/// Prepared immutable filter kernel; finite TailFrames bounds any response
/// generated after excitation stops. It is a policy maximum, not a guarantee
/// that a filter decays to an exact zero by that frame.
/// </summary>
public interface IStreamingFilterStagePlan
{
	int TailFrames { get; }
	IStreamingFilterStageState CreateState();
}

/// <summary>
/// Optional fixed latency for a prepared FIR or other delayed stage.
/// This is descriptive metadata for future host alignment, not automatic
/// compensation of dry signals or a mechanism for delaying host playback.
/// </summary>
public interface ILatencyAwareFilterStagePlan
{
	int LatencyFrames { get; }
}

/// <summary>
/// One channel's mutable filter history. Clone must deep-copy all mutable
/// state. Reconfigure may retain that history when the engine determines
/// identity and structural compatibility, and may throw without corrupting
/// the currently published chain (the engine configures a clone).
/// </summary>
public interface IStreamingFilterStageState
{
	float Process(float sample);
	IStreamingFilterStageState Clone();
	void Reconfigure(IStreamingFilterStagePlan plan);
}

/// <summary>
/// Explicit neutral processor for the engine foundation only. Selecting a
/// filter kind does not yet mean the DSP for that kind exists: the bypass
/// factory intentionally passes all supported kinds through unchanged.
/// Future production integrations must choose real implementations explicitly.
/// </summary>
public sealed class BypassFilterStageFactory : IStreamingFilterStageFactory
{
	public static BypassFilterStageFactory Instance { get; } = new();

	private BypassFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(
		PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
		return BypassPlan.Instance;
	}

	private sealed class BypassPlan : IStreamingFilterStagePlan
	{
		public static BypassPlan Instance { get; } = new();
		public int TailFrames => 0;
		public IStreamingFilterStageState CreateState() => new BypassState();
	}

	private sealed class BypassState : IStreamingFilterStageState
	{
		public float Process(float sample) => sample;
		public IStreamingFilterStageState Clone() => new BypassState();
		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (!ReferenceEquals(plan, BypassPlan.Instance))
				throw new ArgumentException(
					"Bypass state requires a bypass plan.", nameof(plan));
		}
	}
}
