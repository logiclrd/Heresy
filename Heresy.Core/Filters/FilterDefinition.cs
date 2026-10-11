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
/// This is already a SongObject, but the new Filters document-tree root and
/// registration/persistence are separate milestones. Until that root exists,
/// SongDocument.Add deliberately cannot assign Filter objects a placement.
/// </remarks>
public sealed class FilterDefinition : SongObject
{
	private ReadOnlyCollection<FilterStageDefinition> _stages;
	private int _nextStageId;

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

	public FilterStageDefinition GetStage(int stageId)
		=> _stages.FirstOrDefault(stage => stage.StageId == stageId)
			?? throw new KeyNotFoundException($"Unknown filter stage ID {stageId}.");

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

		// A validated replacement is an all-or-nothing definition edit.
		_stages = Array.AsReadOnly(candidate);
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
