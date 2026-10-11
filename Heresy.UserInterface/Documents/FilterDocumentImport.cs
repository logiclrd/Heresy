using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A loaded song and its reusable Filter definitions, ordered by ObjectId.
/// Importing definitions does not import DSP processing state or unrelated
/// song objects.
/// </summary>
public sealed record SongFilterImportSource(
	string Path,
	SongDocument Document,
	IReadOnlyList<FilterDefinition> Filters);

/// <summary>
/// Framework-independent import service for Filter definitions. Selected
/// definitions are cloned with fresh song ObjectIds but identical stable
/// stage IDs, histories of type changes and independent immutable defaults.
/// </summary>
public static class FilterDocumentImport
{
	public static SongFilterImportSource LoadImportSource(string songPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
		string fullPath = Path.GetFullPath(songPath);
		SongDocument source = SongDocumentStorage.Load(fullPath);
		FilterDefinition[] filters = source.Objects
			.OrderBy(pair => pair.Key.Value)
			.Select(pair => pair.Value)
			.OfType<FilterDefinition>()
			.ToArray();
		return new SongFilterImportSource(fullPath, source, filters);
	}

	/// <summary>
	/// Imports only selected definitions, returning old-to-new IDs for
	/// rebinding serialized Filter instances and recursively composed chains.
	/// No definition is attached to the target until all source selections
	/// have been validated and cloned in a separate staging collection.
	/// </summary>
	public static IReadOnlyDictionary<ObjectId, ObjectId> ImportFromSong(
		DocumentWorkspace workspace,
		SongFilterImportSource source,
		IEnumerable<ObjectId> filterIds)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(filterIds);

		HashSet<ObjectId> requested = new(filterIds);
		if (requested.Count == 0)
			return new Dictionary<ObjectId, ObjectId>();
		Dictionary<ObjectId, FilterDefinition> available = [];
		foreach (FilterDefinition filter in source.Filters)
		{
			if (!source.Document.TryGet(filter.Id, out SongObject? actual)
				|| !ReferenceEquals(actual, filter))
				throw new InvalidOperationException(
					$"Filter object {filter.Id.Value} is not attached to the source song.");
			if (!available.TryAdd(filter.Id, filter))
				throw new InvalidOperationException("Duplicate source Filter identity.");
		}
		foreach (ObjectId id in requested)
			if (!available.ContainsKey(id))
				throw new InvalidOperationException(
					$"Source song has no Filter definition {id.Value}.");

		FilterDefinition[] selected = requested.OrderBy(id => id.Value)
			.Select(id => available[id]).ToArray();

		// Cloning validates all stage schemas before any new objects are added.
		// Allocate target IDs only after a complete source validation pass.
		FilterDefinition[] validated = selected.Select(filter =>
			CloneDefinition(filter, filter.Id)).ToArray();
		Dictionary<ObjectId, ObjectId> idMap = [];
		foreach (FilterDefinition filter in validated)
			idMap.Add(filter.Id, workspace.Document.AllocateObjectId());
		foreach (FilterDefinition filter in validated)
			workspace.Document.Add(CloneDefinition(filter, idMap[filter.Id]));

		return idMap;
	}

	private static FilterDefinition CloneDefinition(FilterDefinition source, ObjectId newId)
	{
		FilterStageDefinition[] stages = source.Stages.Select(stage =>
			new FilterStageDefinition(
				stage.StageId, stage.Name, stage.Kind, stage.DefaultParameters))
			.ToArray();
		FilterDefinition copied = new(newId, source.Name, stages);
		copied.RestoreStageIdentity(
			source.NextStageIdForPersistence,
			source.StageGenerationsForPersistence);
		return copied;
	}
}
