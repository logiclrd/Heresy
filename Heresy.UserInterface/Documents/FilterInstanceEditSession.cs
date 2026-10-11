using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// A reusable copy-on-write authoring session for one configured Filter use.
/// The owning Instrument, FM node, effect, or output supplies a getter and
/// an atomic publication callback. No owner exists in Core yet, so this
/// service does not silently mutate a SongDocument or mark its revisions.
/// The owner publishes a new configuration and marks the appropriate revision.
/// </summary>
public sealed class FilterInstanceEditSession
{
	private readonly Func<FilterInstance> _readCurrent;
	private readonly Action<FilterInstance> _publish;
	private FilterInstance _baseline = null!;
	private FilterStageDefinition[] _baselineStages = [];
	private long _baselineRevision;

	public FilterInstanceEditSession(
		Func<FilterInstance> readCurrent,
		Action<FilterInstance> publish)
	{
		_readCurrent = readCurrent ?? throw new ArgumentNullException(nameof(readCurrent));
		_publish = publish ?? throw new ArgumentNullException(nameof(publish));
		Reload();
	}

	public FilterInstance Current => _baseline;
	public IReadOnlyList<FilterStageDefinition> Stages => Array.AsReadOnly(_baselineStages);
	public ObjectId DefinitionId => _baseline.DefinitionId;

	/// <summary>The loaded configuration or its shared definition changed.
	/// A caller may display this conflict without discarding local field edits.</summary>
	public bool IsStale
	{
		get
		{
			FilterInstance current = _readCurrent();
			return current is null
				|| !ReferenceEquals(current, _baseline)
				|| current.Revision != _baselineRevision
				|| !current.Definition.Stages.SequenceEqual(_baselineStages);
		}
	}

	public void Reload()
	{
		FilterInstance current = _readCurrent()
			?? throw new InvalidOperationException("The configured Filter use is missing.");
		_baseline = current;
		_baselineRevision = current.Revision;
		_baselineStages = current.Definition.Stages.ToArray();
	}

	/// <summary>
	/// Validate the complete new set of explicit overrides on an isolated
	/// clone. On success publish a *new* instance once, never the existing
	/// mutable instance. Stale or invalid drafts do not publish at all.
	/// </summary>
	public bool ApplyStage(
		int stageId,
		IEnumerable<KeyValuePair<string, FilterParameterValue>> overrides)
	{
		ArgumentNullException.ThrowIfNull(overrides);
		if (IsStale)
			throw new InvalidOperationException(
				"This Filter use or its shared definition changed in another editor. "
				+ "Reload before applying your draft.");

		FilterInstance proposed = _baseline.Clone();
		bool changed = proposed.ReplaceStageOverrides(stageId, overrides);
		if (!changed)
			return false;
		// The owning object must atomically replace its configuration and
		// increment its song revision, if applicable. A failed publication
		// leaves the old owner and this editor's baseline untouched.
		_publish(proposed);
		Reload();
		return true;
	}
}
