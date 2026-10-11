using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.Core.Filters;

/// <summary>
/// A named, reusable serial filter definition. A stage identity remains stable
/// across rename and reordering; changing its Kind keeps its identity but
/// invalidates per-use overrides for the previous kind.
/// </summary>
/// <remarks>
/// Persisted identity metadata includes both the next stage ID and prior
/// type generations, so reopening a song never resurrects stale overrides.
/// </remarks>
public sealed class FilterDefinition : SongObject
{
	private ReadOnlyCollection<FilterStageDefinition> _stages;
	private int _nextStageId;
	private Dictionary<int, long> _stageGenerations = [];

	public FilterDefinition(
		ObjectId id,
		string name,
		IEnumerable<FilterStageDefinition>? stages = null)
		: base(id, name)
	{
		_stages = Array.AsReadOnly<FilterStageDefinition>([]);
		ReplaceStages(stages ?? new[] {
			new FilterStageDefinition(1, "stage1", FilterKind.OnePole),
		});
	}

	public override SongObjectKind Kind => SongObjectKind.Filter;
	public IReadOnlyList<FilterStageDefinition> Stages => _stages;

	/// <summary>Monotonic stage-ID allocator state, including removed IDs.</summary>
	public int NextStageIdForPersistence => _nextStageId;

	/// <summary>Generation counters also include removed stages whose IDs
	/// must not regain earlier parameter overrides after restoration.</summary>
	public IReadOnlyDictionary<int, long> StageGenerationsForPersistence
		=> new System.Collections.ObjectModel.ReadOnlyDictionary<int, long>(
			new Dictionary<int, long>(_stageGenerations));

	/// <summary>Restore validated identity metadata without changing stage
	/// order, names or defaults. Called after constructing the stages from a
	/// persisted definition; never resets the live document's revisions.</summary>
	public void RestoreStageIdentity(
		int nextStageId,
		IReadOnlyDictionary<int, long> generations)
	{
		ArgumentNullException.ThrowIfNull(generations);
		if (nextStageId < _nextStageId)
			throw new ArgumentOutOfRangeException(nameof(nextStageId),
				"Next stage ID cannot precede a persisted stage ID.");
		Dictionary<int, long> restored = [];
		foreach ((int id, long generation) in generations)
		{
			if (id <= 0 || id >= nextStageId || generation < 0)
				throw new ArgumentException(
					"Invalid persisted stage generation metadata.", nameof(generations));
			restored.Add(id, generation);
		}
		foreach (FilterStageDefinition stage in _stages)
			if (!restored.ContainsKey(stage.StageId))
				throw new ArgumentException(
					$"Missing generation for stage {stage.StageId}.", nameof(generations));
		_stageGenerations = restored;
		_nextStageId = nextStageId;
	}

	public FilterStageDefinition GetStage(int stageId)
		=> _stages.FirstOrDefault(stage => stage.StageId == stageId)
			?? throw new KeyNotFoundException($"Unknown filter stage ID {stageId}.");

	internal long GetStageGeneration(int stageId)
	{
		GetStage(stageId);
		return _stageGenerations[stageId];
	}

	public FilterStageDefinition GetStage(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return _stages.FirstOrDefault(stage =>
			string.Equals(stage.Name, name, StringComparison.OrdinalIgnoreCase))
			?? throw new KeyNotFoundException($"Unknown filter stage '{name}'.");
	}

	/// <summary>
	/// Validates the complete proposed serial order before replacing anything.
	/// The next automatic ID remains monotonic, even after deleting stages.
	/// </summary>
	public void ReplaceStages(IEnumerable<FilterStageDefinition> stages)
	{
		ArgumentNullException.ThrowIfNull(stages);
		FilterStageDefinition[] candidate = stages.ToArray();
		if (candidate.Length == 0)
			throw new ArgumentException(
				"A Filter must contain at least one stage.", nameof(stages));
		HashSet<int> ids = [];
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		int maximumId = 0;
		foreach (FilterStageDefinition stage in candidate)
		{
			if (stage is null)
				throw new ArgumentException(
					"Filter stages may not be null.", nameof(stages));
			if (!ids.Add(stage.StageId))
				throw new ArgumentException(
					$"Duplicate stage ID {stage.StageId}.", nameof(stages));
			if (!names.Add(stage.Name))
				throw new ArgumentException(
					$"Duplicate stage name '{stage.Name}'.", nameof(stages));
			maximumId = Math.Max(maximumId, stage.StageId);
		}

		// Keep a generation even for removed stages: an old instance override
		// cannot reappear if its ID is later reused with the original kind,
		// or if a stage switches kind twice between accesses.
		Dictionary<int, long> nextGenerations = new(_stageGenerations);
		Dictionary<int, FilterStageDefinition> previous =
			_stages.ToDictionary(stage => stage.StageId);
		foreach (FilterStageDefinition stage in candidate)
		{
			if (!_stageGenerations.TryGetValue(stage.StageId, out long generation))
				nextGenerations[stage.StageId] = 0;
			else if (!previous.TryGetValue(stage.StageId,
				out FilterStageDefinition? existing) || existing.Kind != stage.Kind)
				nextGenerations[stage.StageId] = checked(generation + 1);
		}

		// A validated replacement is an all-or-nothing definition edit.
		_stages = Array.AsReadOnly(candidate);
		_stageGenerations = nextGenerations;
		_nextStageId = maximumId == int.MaxValue
			? int.MaxValue
			: Math.Max(_nextStageId, maximumId + 1);
	}

	public FilterStageDefinition AddStage(
		string name,
		FilterKind kind)
	{
		if (_nextStageId == int.MaxValue)
			throw new InvalidOperationException("Filter stage ID space exhausted.");
		FilterStageDefinition stage = new(_nextStageId, name, kind);
		ReplaceStages(_stages.Concat(new[] { stage }));
		return stage;
	}

	public void RemoveStage(int stageId)
	{
		GetStage(stageId);
		ReplaceStages(_stages.Where(stage => stage.StageId != stageId));
	}

	public void RenameStage(int stageId, string name)
	{
		ReplaceStage(stageId, GetStage(stageId).Rename(name));
	}

	public void ChangeStageKind(int stageId, FilterKind kind)
	{
		ReplaceStage(stageId, GetStage(stageId).ChangeKind(kind));
	}

	public void SetStageDefaults(
		int stageId,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> assignments)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		ReplaceStage(stageId, GetStage(stageId).WithDefaults(assignments));
	}

	public void MoveStage(int stageId, int newIndex)
	{
		GetStage(stageId);
		if (newIndex < 0 || newIndex >= _stages.Count)
			throw new ArgumentOutOfRangeException(nameof(newIndex));
		List<FilterStageDefinition> ordered = [.. _stages];
		FilterStageDefinition stage = ordered.Single(s => s.StageId == stageId);
		ordered.Remove(stage);
		ordered.Insert(newIndex, stage);
		ReplaceStages(ordered);
	}

	private void ReplaceStage(int stageId, FilterStageDefinition replacement)
		=> ReplaceStages(_stages.Select(stage =>
			stage.StageId == stageId ? replacement : stage));
}
