using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;

namespace Heresy.Core.FmSynthesis;

/// <summary>A durable FM modulation wire bound to the immutable identity of
/// a Filter stage and parameter, not its visible name or input ordinal.</summary>
public sealed record FmFilterParameterConnection(
	int StageId, FilterKind Kind, long Generation, string ParameterName,
	int SourceNodeId);

/// <summary>A currently available graph port, ordered as audio Input then
/// continuously modulatable stage parameters in shared serial stage order.</summary>
public sealed record FmFilterInputPort(
	int Index, int StageId, FilterKind Kind, long Generation,
	string ParameterName, int? SourceNodeId)
{
	public bool IsAudio => Index == 0;
}

/// <summary>
/// Authoring-only Filter graph node. Its nullable configured use is owned by
/// this node and never aliases another editor's overrides. Stale modulation
/// wires remain identifiable after definition kind changes but are excluded
/// from the active graph rather than rebound to unrelated parameters.
/// </summary>
public sealed class FmFilterNode : FmSynthNode
{
	private readonly FilterInstance? _configuration;
	private readonly IReadOnlyList<FmFilterParameterConnection> _connections;

	public FmFilterNode(int id, int? audioInputNodeId = null,
		FilterInstance? configuration = null,
		IEnumerable<FmFilterParameterConnection>? connections = null,
		double tailSeconds = 0)
		: base(id)
	{
		if (audioInputNodeId < 0)
			throw new ArgumentOutOfRangeException(nameof(audioInputNodeId));
		if (!double.IsFinite(tailSeconds) || tailSeconds < 0)
			throw new ArgumentOutOfRangeException(nameof(tailSeconds));
		TailSeconds = tailSeconds;
		AudioInputNodeId = audioInputNodeId;
		_configuration = configuration?.Clone();
		FmFilterParameterConnection[] items = (connections
			?? Array.Empty<FmFilterParameterConnection>()).ToArray();
		HashSet<(int, long, FilterKind, string)> seen = [];
		foreach (FmFilterParameterConnection item in items)
		{
			if (item is null || item.StageId <= 0 || item.Generation < 0
				|| !Enum.IsDefined(item.Kind)
				|| string.IsNullOrWhiteSpace(item.ParameterName)
				|| item.SourceNodeId < 0
				|| !seen.Add((item.StageId, item.Generation, item.Kind,
					item.ParameterName.ToUpperInvariant())))
				throw new ArgumentException(
					"Invalid or duplicate FM Filter modulation port identity.",
					nameof(connections));
		}
		_connections = Array.AsReadOnly(items);
	}

	public int? AudioInputNodeId { get; }
	public double TailSeconds { get; }
	/// <summary>Owned configuration. Consumers must publish a replacement;
	/// direct mutation is detectable by its Revision but unsupported.</summary>
	public FilterInstance? Configuration => _configuration;
	public IReadOnlyList<FmFilterParameterConnection> Connections => _connections;

	/// <summary>Re-evaluate available ports against the shared definition
	/// whenever its topology changes. Stage ID+generation+kind is mandatory.</summary>
	public IReadOnlyList<FmFilterInputPort> GetPorts()
	{
		List<FmFilterInputPort> result =
			[new FmFilterInputPort(0, 0, default, 0, "Input", AudioInputNodeId)];
		if (_configuration is null)
			return result.AsReadOnly();

		foreach (ConfiguredFilterStage stage in _configuration.GetStages())
		{
			long generation =
				_configuration.GetStageGenerationForPersistence(stage.StageId);
			foreach (FilterParameterDescriptor parameter in stage.Parameters.Parameters)
			{
				if (!parameter.IsModulatable)
					continue;
				FmFilterParameterConnection? wire = _connections.FirstOrDefault(
					item => item.StageId == stage.StageId
						&& item.Kind == stage.Kind
						&& item.Generation == generation
						&& string.Equals(item.ParameterName, parameter.Name,
							StringComparison.OrdinalIgnoreCase));
				result.Add(new FmFilterInputPort(
					result.Count, stage.StageId, stage.Kind, generation,
					parameter.Name, wire?.SourceNodeId));
			}
		}
		return result.AsReadOnly();
	}

	/// <summary>All authoring connections, including incompatible ones,
	/// are kept for diagnostics; only currently valid edges affect the DAG.</summary>
	public IReadOnlyList<FmFilterParameterConnection> StaleConnections
	{
		get
		{
			HashSet<(int, long, FilterKind, string)> active =
				GetPorts().Skip(1).Select(port => (
					port.StageId, port.Generation, port.Kind,
					port.ParameterName.ToUpperInvariant())).ToHashSet();
			return _connections.Where(wire => !active.Contains((
				wire.StageId, wire.Generation, wire.Kind,
				wire.ParameterName.ToUpperInvariant()))).ToArray();
		}
	}

	public override IReadOnlyList<int> InputNodeIds =>
		GetPorts().Where(port => port.SourceNodeId.HasValue)
			.Select(port => port.SourceNodeId!.Value).ToArray();

	public FmFilterNode WithAudioInput(int? sourceId)
		=> new(Id, sourceId, _configuration, _connections, TailSeconds);

	public FmFilterNode WithConfiguration(FilterInstance? configuration)
		=> new(Id, AudioInputNodeId, configuration,
			configuration?.DefinitionId == _configuration?.DefinitionId
				? _connections : null, TailSeconds);

	public FmFilterNode WithTail(double seconds)
		=> new(Id, AudioInputNodeId, _configuration, _connections, seconds);

	public FmFilterNode WithParameterConnection(
		FmFilterInputPort port, int? sourceId)
	{
		ArgumentNullException.ThrowIfNull(port);
		if (port.IsAudio)
			return WithAudioInput(sourceId);
		if (!GetPorts().Any(candidate =>
			candidate.Index == port.Index
			&& candidate.StageId == port.StageId
			&& candidate.Generation == port.Generation
			&& candidate.Kind == port.Kind
			&& candidate.ParameterName == port.ParameterName))
			throw new InvalidOperationException(
				"The FM Filter input port was invalidated by a definition edit.");

		List<FmFilterParameterConnection> next = _connections.Where(item =>
			item.StageId != port.StageId
			|| item.Generation != port.Generation
			|| item.Kind != port.Kind
			|| !string.Equals(item.ParameterName, port.ParameterName,
				StringComparison.OrdinalIgnoreCase)).ToList();
		if (sourceId.HasValue)
			next.Add(new FmFilterParameterConnection(
				port.StageId, port.Kind, port.Generation,
				port.ParameterName, sourceId.Value));
		return new FmFilterNode(Id, AudioInputNodeId, _configuration, next,
			TailSeconds);
	}
}
