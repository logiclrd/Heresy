using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Atomic, framework-independent authoring surface for shared Filter
/// definitions. Editors pass the stage they originally displayed to prevent
/// stale drafts from overwriting newer edits to that same stage.
/// </summary>
public static class FilterDocumentEditor
{
	public static FilterDefinition CreateFilter(
		DocumentWorkspace workspace, string name)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		string normalized = name.Trim();
		// Validate before allocating an ID; the final instance receives its
		// real ID and a fresh, independent stage collection.
		_ = new FilterDefinition((ObjectId)1U, normalized);
		FilterDefinition result = new(workspace.Document.AllocateObjectId(), normalized);
		workspace.Document.Add(result, affectsAudio: true);
		return result;
	}

	public static FilterStageDefinition AddStage(
		DocumentWorkspace workspace, FilterDefinition definition,
		FilterKind kind = FilterKind.OnePole)
	{
		ValidateAttached(workspace, definition);
		_ = FilterCatalogue.GetParameters(kind);
		int nextId = definition.NextStageIdForPersistence;
		if (nextId == int.MaxValue)
			throw new InvalidOperationException("Filter stage IDs are exhausted.");
		string name = $"stage{nextId}";
		HashSet<string> names = definition.Stages
			.Select(stage => stage.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		while (names.Contains(name))
			name += "_";
		FilterStageDefinition added = definition.AddStage(name, kind);
		workspace.Document.MarkChanged(affectsAudio: true);
		return added;
	}

	public static bool RemoveStage(
		DocumentWorkspace workspace, FilterDefinition definition,
		int stageId, FilterStageDefinition expected)
	{
		RequireUnchanged(workspace, definition, stageId, expected);
		// Last-stage removal is rejected atomically by FilterDefinition.
		definition.RemoveStage(stageId);
		workspace.Document.MarkChanged(affectsAudio: true);
		return true;
	}

	public static bool MoveStage(
		DocumentWorkspace workspace, FilterDefinition definition,
		int stageId, int targetIndex, FilterStageDefinition expected)
	{
		RequireUnchanged(workspace, definition, stageId, expected);
		int current = definition.Stages.ToList()
			.FindIndex(stage => stage.StageId == stageId);
		if (targetIndex < 0 || targetIndex >= definition.Stages.Count)
			throw new ArgumentOutOfRangeException(nameof(targetIndex));
		if (current == targetIndex)
			return false;
		definition.MoveStage(stageId, targetIndex);
		workspace.Document.MarkChanged(affectsAudio: true);
		return true;
	}

	/// <summary>
	/// Replaces name, filter type and full default parameter set as one
	/// validated stage edit. Invalid fields or shared-editor conflicts leave
	/// all content and revision counters unchanged.
	/// </summary>
	public static bool ApplyStage(
		DocumentWorkspace workspace, FilterDefinition definition,
		int stageId, FilterStageDefinition expected,
		string name, FilterKind kind,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> defaults)
	{
		RequireUnchanged(workspace, definition, stageId, expected);
		ArgumentNullException.ThrowIfNull(defaults);
		FilterStageDefinition candidate = new(stageId, name, kind, defaults);
		if (Equivalent(expected, candidate))
			return false;

		// Atomic duplicate-name and parameter-combination validation takes
		// place in the candidate and in ReplaceStages, before any commit.
		definition.ReplaceStages(definition.Stages.Select(stage =>
			stage.StageId == stageId ? candidate : stage));
		workspace.Document.MarkChanged(affectsAudio: true);
		return true;
	}

	public static void ValidateAttached(
		DocumentWorkspace workspace, FilterDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(definition);
		if (!workspace.Document.TryGet(definition.Id, out SongObject? live)
			|| !ReferenceEquals(live, definition))
			throw new InvalidOperationException(
				"This Filter definition no longer belongs to the active song.");
	}

	private static void RequireUnchanged(
		DocumentWorkspace workspace, FilterDefinition definition,
		int stageId, FilterStageDefinition expected)
	{
		ValidateAttached(workspace, definition);
		ArgumentNullException.ThrowIfNull(expected);
		if (expected.StageId != stageId ||
			!definition.Stages.Any(stage =>
				stage.StageId == stageId && ReferenceEquals(stage, expected)))
			throw new InvalidOperationException(
				"The shared Filter stage changed in another editor. Reload before applying.");
	}

	private static bool Equivalent(
		FilterStageDefinition a, FilterStageDefinition b)
		=> a.Name == b.Name && a.Kind == b.Kind
			&& a.DefaultParameters.Count == b.DefaultParameters.Count
			&& a.DefaultParameters.All(pair =>
				b.DefaultParameters.TryGetValue(pair.Key,
					out FilterParameterValue? value) && value == pair.Value);
}
