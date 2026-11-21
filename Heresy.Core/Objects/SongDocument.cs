using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.Objects;

/// <summary>
/// Mutable authoring document. Playback layers are expected to take snapshots
/// or otherwise capture definitions at invocation boundaries.
/// </summary>
public sealed class SongDocumentChangedEventArgs(
	bool affectsAudio,
	uint documentRevision,
	uint audioRevision) : EventArgs
{
	public bool AffectsAudio { get; } = affectsAudio;
	public uint DocumentRevision { get; } = documentRevision;
	public uint AudioRevision { get; } = audioRevision;
}

public sealed class SongDocument
{
	private readonly Dictionary<ObjectId, SongObject> _objects = [];
	private readonly Dictionary<ObjectId, ObjectTombstone> _tombstones = [];
	private readonly Dictionary<SongTreeSection, SongTreeFolder> _sectionRoots = [];
	private uint _nextObjectId = 1;

	// Pre-release schema changes intentionally remain version 1 until the format is stabilized.
	public const int FormatVersion = 1;

	public SongDocument()
	{
		Root = new SongTreeFolder("Song");
		foreach (SongTreeSection section in SongTreeSections.DocumentOrder)
		{
			SongTreeFolder sectionRoot =
				new(SongTreeSections.GetName(section));
			_sectionRoots.Add(section, sectionRoot);
			Root.Children.Add(sectionRoot);
		}
	}

	public IReadOnlyDictionary<ObjectId, SongObject> Objects => _objects;
	public IReadOnlyDictionary<ObjectId, ObjectTombstone> Tombstones => _tombstones;

	public SongTreeFolder Root { get; }

	public ObjectId RootSequenceId { get; set; }

	/// <summary>Raised on the editing thread after revisions change.</summary>
	public event EventHandler<SongDocumentChangedEventArgs>? Changed;

	public uint DocumentRevision { get; private set; }
	public uint AudioRevision { get; private set; }

	internal uint NextObjectIdForPersistence => _nextObjectId;

	/// <summary>The next ID that AllocateObjectId would return, without
	/// allocating it or changing the document. Import preparation may build a
	/// provisional assignment from this value, but must check freshness again
	/// before any commit: this is NOT an ID reservation.</summary>
	public ObjectId NextUnallocatedObjectId => new(_nextObjectId);

	public SongTreeFolder GetSectionRoot(SongTreeSection section)
		=> _sectionRoots.TryGetValue(section, out SongTreeFolder? root)
			? root
			: throw new ArgumentOutOfRangeException(nameof(section));

	public ObjectId AllocateObjectId()
	{
		if (_nextObjectId == 0)
			throw new InvalidOperationException("The song has exhausted its object ID space.");

		return new ObjectId(_nextObjectId++);
	}

	/// <summary>Resolve and validate a creation/import destination in the
	/// correct section before allocating an ID or publishing any object.</summary>
	public SongTreeFolder ResolveObjectDestination(
		SongTreeSection section, SongTreeFolder? destinationFolder = null)
	{
		SongTreeFolder root = GetSectionRoot(section);
		SongTreeFolder destination = destinationFolder ?? root;
		if (!IsFolderInSection(root, destination))
			throw new InvalidOperationException(
				"The destination folder is not attached to the requested song section.");
		return destination;
	}

	public void Add(SongObject songObject, bool affectsAudio = true,
		SongTreeFolder? destinationFolder = null)
	{
		ArgumentNullException.ThrowIfNull(songObject);

		SongTreeFolder destination = ResolveObjectDestination(
			SongTreeSections.ForKind(songObject.Kind), destinationFolder);

		if (!_objects.TryAdd(songObject.Id, songObject))
			throw new InvalidOperationException($"Object ID {songObject.Id} is already in use.");

		_tombstones.Remove(songObject.Id);
		EnsureNextIdPast(songObject.Id);
		destination.Children.Add(
			new SongTreeObject(songObject.Name, songObject.Id));
		MarkChanged(affectsAudio);
	}

	public bool TryGet(ObjectId id, out SongObject? songObject)
		=> _objects.TryGetValue(id, out songObject);

	public bool Remove(ObjectId id, bool affectsAudio = true)
	{
		if (!_objects.Remove(id, out SongObject? removed))
			return false;

		RemoveTreePlacements(Root, id);
		_tombstones[id] = new ObjectTombstone(id, removed.Name, removed.Kind);
		MarkChanged(affectsAudio);
		return true;
	}

	public void MarkChanged(bool affectsAudio)
	{
		unchecked
		{
			DocumentRevision++;
			if (affectsAudio)
				AudioRevision++;
		}
		Changed?.Invoke(this, new SongDocumentChangedEventArgs(
			affectsAudio, DocumentRevision, AudioRevision));
	}

	/// <summary>
	/// Publish fully prepared imported objects as one document edit. No object,
	/// tree placement, ID allocation, or Changed notification is exposed until
	/// every candidate has been verified. An exception rolls back the original
	/// cursor, revisions, placements and object collection.
	///
	/// The caller must run complete import preflight, then supply a freshness
	/// callback which is evaluated at the mutation boundary. This document's
	/// standard editing model is single-threaded; this operation does not make
	/// arbitrary mutable SongObject fields safe for concurrent writers.
	/// </summary>
	public SongImportBatchEdit CommitImportedObjects(
		IReadOnlyList<SongObject> candidates, ObjectId expectedNextId,
		Func<SongDocument, bool> isCurrent,
		IReadOnlyList<SongImportTreePlacement?>? treePlacements = null)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(isCurrent);
		if (treePlacements is not null && treePlacements.Count != candidates.Count)
			throw new ArgumentException(
				"Placement count must equal the candidate count.", nameof(treePlacements));
		if (candidates.Count == 0)
			throw new ArgumentException(
				"Import requires at least one copied object.", nameof(candidates));
		if (_nextObjectId != expectedNextId.Value || _nextObjectId == 0)
			throw new InvalidOperationException(
				"The destination ID cursor changed after import preparation.");
		if (!isCurrent(this))
			throw new InvalidOperationException(
				"A source or destination song changed after import preparation.");

		SongObject[] objects = candidates.ToArray();
		SongImportBatchEdit.Placement[] placements =
			new SongImportBatchEdit.Placement[objects.Length];
		if ((ulong)_nextObjectId + (ulong)objects.Length - 1 > uint.MaxValue)
			throw new InvalidOperationException("Import exceeds the destination ID range.");
		HashSet<ObjectId> unique = [];
		for (int index = 0; index < objects.Length; index++)
		{
			SongObject candidate = objects[index]
				?? throw new ArgumentException("Import includes a null object.", nameof(candidates));
			ObjectId desired = new((uint)((ulong)_nextObjectId + (ulong)index));
			if (candidate.Id != desired || !unique.Add(candidate.Id)
				|| _objects.ContainsKey(candidate.Id)
				|| _tombstones.ContainsKey(candidate.Id))
				throw new InvalidOperationException(
					$"Import ID #{candidate.Id.Value} is not an unused contiguous destination ID.");
			SongTreeFolder section = GetSectionRoot(
				SongTreeSections.ForKind(candidate.Kind));
			SongImportTreePlacement? position = treePlacements?[index];
			SongTreeFolder folder = position?.Parent ?? section;
			if (!IsFolderInSection(section, folder))
				throw new InvalidOperationException(
					"The import placement folder is not attached to the correct song section.");
			if (position?.InsertAfter is SongTreeNode anchor
				&& !folder.Children.Contains(anchor))
				throw new InvalidOperationException(
					"The import placement anchor is not a child of its folder.");
			placements[index] = new SongImportBatchEdit.Placement(
				candidate, folder, new SongTreeObject(candidate.Name, candidate.Id),
				position?.InsertAfter);
		}

		uint previousNext = _nextObjectId;
		uint previousDocumentRevision = DocumentRevision;
		uint previousAudioRevision = AudioRevision;
		string originalSnapshot =
			Heresy.Core.Persistence.SongDocumentJson.SerializeAuthoringSnapshot(this);
		int installed = 0;
		try
		{
			foreach (SongImportBatchEdit.Placement placement in placements)
			{
				_objects.Add(placement.Object.Id, placement.Object);
				installed++;
				InsertImportPlacement(placement);
			}
			ulong next = (ulong)previousNext + (ulong)placements.Length;
			_nextObjectId = next > uint.MaxValue ? 0 : (uint)next;
			MarkChanged(affectsAudio: true);
			string committedSnapshot =
				Heresy.Core.Persistence.SongDocumentJson.SerializeAuthoringSnapshot(this);
			return new SongImportBatchEdit(this, placements,
				previousNext, previousDocumentRevision, previousAudioRevision,
				originalSnapshot, committedSnapshot,
				DocumentRevision, AudioRevision);
		}
		catch
		{
			for (int index = installed - 1; index >= 0; index--)
			{
				SongImportBatchEdit.Placement placement = placements[index];
				placement.Section.Children.Remove(placement.TreeNode);
				_objects.Remove(placement.Object.Id);
			}
			_nextObjectId = previousNext;
			DocumentRevision = previousDocumentRevision;
			AudioRevision = previousAudioRevision;
			throw;
		}
	}

	/// <summary>Resolve an old-sibling anchor at the point of insertion, so
	/// a deep batch can place multiple copies in one folder without stale
	/// numeric indices. Null means append to the selected parent.</summary>
	private static void InsertImportPlacement(SongImportBatchEdit.Placement placement)
	{
		int index = placement.InsertAfter is null
			? placement.Section.Children.Count
			: placement.Section.Children.IndexOf(placement.InsertAfter) + 1;
		if (index < 0 || (placement.InsertAfter is not null && index == 0))
			throw new InvalidOperationException(
				"The saved import placement anchor is missing.");
		placement.Section.Children.Insert(index, placement.TreeNode);
	}

	private static bool IsFolderInSection(
		SongTreeFolder section, SongTreeFolder candidate)
	{
		if (ReferenceEquals(section, candidate))
			return true;
		foreach (SongTreeNode child in section.Children)
			if (child is SongTreeFolder folder
				&& IsFolderInSection(folder, candidate))
				return true;
		return false;
	}

	/// <summary>Import undo and redo use the same no-intermediate-publish path,
	/// with a fresh Changed notification and one audio/document revision each.
	/// They refuse to erase unrelated intervening edits.</summary>
	internal void ReplayImportBatch(SongImportBatchEdit edit, bool apply)
	{
		if (!ReferenceEquals(edit.Document, this))
			throw new InvalidOperationException("Import edit belongs to another song.");
		if (_nextObjectId != edit.ExpectedNextId
			|| DocumentRevision != edit.ExpectedDocumentRevision
			|| AudioRevision != edit.ExpectedAudioRevision
			|| !string.Equals(
				Heresy.Core.Persistence.SongDocumentJson.SerializeAuthoringSnapshot(this),
				edit.ExpectedSnapshot, StringComparison.Ordinal))
			throw new InvalidOperationException(
				"The song changed since this import undo/redo state was recorded.");

		uint priorNext = _nextObjectId;
		uint priorDocumentRevision = DocumentRevision;
		uint priorAudioRevision = AudioRevision;
		int changed = 0;
		try
		{
			if (apply)
			{
				foreach (SongImportBatchEdit.Placement placement in edit.Placements)
				{
					if (_objects.ContainsKey(placement.Object.Id)
						|| _tombstones.ContainsKey(placement.Object.Id))
						throw new InvalidOperationException("An import ID is occupied.");
				}
				foreach (SongImportBatchEdit.Placement placement in edit.Placements)
				{
					_objects.Add(placement.Object.Id, placement.Object);
					changed++;
					InsertImportPlacement(placement);
				}
				ulong next = (ulong)edit.InitialNextId + (ulong)edit.Placements.Count;
				_nextObjectId = next > uint.MaxValue ? 0 : (uint)next;
			}
			else
			{
				foreach (SongImportBatchEdit.Placement placement in edit.Placements)
				{
					if (!_objects.TryGetValue(placement.Object.Id, out SongObject? current)
						|| !ReferenceEquals(current, placement.Object)
						|| !placement.Section.Children.Contains(placement.TreeNode))
						throw new InvalidOperationException(
							"An imported object or its tree placement was replaced.");
				}
				foreach (SongImportBatchEdit.Placement placement in edit.Placements)
				{
					placement.Section.Children.Remove(placement.TreeNode);
					_objects.Remove(placement.Object.Id);
					changed++;
				}
				_nextObjectId = edit.InitialNextId;
			}
			MarkChanged(affectsAudio: true);
			edit.Advance(apply, _nextObjectId, DocumentRevision, AudioRevision,
				Heresy.Core.Persistence.SongDocumentJson.SerializeAuthoringSnapshot(this));
		}
		catch
		{
			if (apply)
			{
				for (int index = changed - 1; index >= 0; index--)
				{
					SongImportBatchEdit.Placement placement = edit.Placements[index];
					placement.Section.Children.Remove(placement.TreeNode);
					_objects.Remove(placement.Object.Id);
				}
			}
			else
			{
				// Undo removes in original order, so restoring in original
				// order is essential: reversed restoration would silently
				// reorder two sibling tree placements after a failed undo.
				for (int index = 0; index < changed; index++)
				{
					SongImportBatchEdit.Placement placement = edit.Placements[index];
					_objects.Add(placement.Object.Id, placement.Object);
					InsertImportPlacement(placement);
				}
			}
			_nextObjectId = priorNext;
			DocumentRevision = priorDocumentRevision;
			AudioRevision = priorAudioRevision;
			throw;
		}
	}

	/// <summary>
	/// Drops tombstones which no longer have references. The caller supplies
	/// the set because reference discovery belongs to the serializer/compiler.
	/// </summary>
	public void PruneTombstones(IReadOnlySet<ObjectId> referencedIds)
	{
		ObjectId[] remove = _tombstones.Keys.Where(id => !referencedIds.Contains(id)).ToArray();
		foreach (ObjectId id in remove)
			_tombstones.Remove(id);
	}

	internal void RestoreObject(SongObject songObject)
	{
		ArgumentNullException.ThrowIfNull(songObject);
		if (!_objects.TryAdd(songObject.Id, songObject))
			throw new InvalidOperationException($"Object ID {songObject.Id} is already in use.");
		if (_tombstones.ContainsKey(songObject.Id))
			throw new InvalidOperationException($"Object ID {songObject.Id} also exists as a tombstone.");
		SongTreeSections.ForKind(songObject.Kind);
		EnsureNextIdPast(songObject.Id);
	}

	internal void RestoreTombstone(ObjectTombstone tombstone)
	{
		if (tombstone.Id.IsNone)
			throw new ArgumentException("Tombstones may not use ObjectId.None.", nameof(tombstone));
		if (_objects.ContainsKey(tombstone.Id))
			throw new InvalidOperationException($"Object ID {tombstone.Id} already exists as an object.");
		if (!_tombstones.TryAdd(tombstone.Id, tombstone))
			throw new InvalidOperationException($"Object ID {tombstone.Id} is already tombstoned.");
		EnsureNextIdPast(tombstone.Id);
	}

	internal void RestoreTree(SongTreeFolder restoredRoot)
	{
		ArgumentNullException.ThrowIfNull(restoredRoot);

		// Version-1 documents may have the original four roots (with
		// Envelopes mixed into Instruments), or the later five-root layout.
		// Upgrade both without allocating IDs or changing revision counters.
		if (restoredRoot.Children.Count == 4)
			restoredRoot = UpgradeLegacyFourSectionTree(restoredRoot);
		if (restoredRoot.Children.Count == 5)
			restoredRoot = UpgradeLegacyFiveSectionTree(restoredRoot);

		if (restoredRoot.Children.Count != SongTreeSections.DocumentOrder.Length)
		{
			throw new InvalidOperationException(
				"A song tree must contain exactly the six fixed document sections.");
		}

		for (int index = 0; index < SongTreeSections.DocumentOrder.Length; index++)
		{
			SongTreeSection section = SongTreeSections.DocumentOrder[index];
			if (restoredRoot.Children[index] is not SongTreeFolder restoredSection
				|| !string.Equals(
					restoredSection.Name,
					SongTreeSections.GetName(section),
					StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					$"Song-tree section {index} must be '{SongTreeSections.GetName(section)}'.");
			}

			SongTreeFolder target = GetSectionRoot(section);
			target.Children.Clear();
			target.Children.AddRange(restoredSection.Children);
		}

		Root.Name = restoredRoot.Name;
	}

	private SongTreeFolder UpgradeLegacyFourSectionTree(
		SongTreeFolder oldRoot)
	{
		string[] expected = ["Sequences", "Patterns", "Instruments", "Samples"];
		for (int index = 0; index < expected.Length; index++)
		{
			if (oldRoot.Children[index] is not SongTreeFolder folder
				|| folder.Name != expected[index])
			{
				throw new InvalidOperationException(
					$"Legacy song-tree section {index} must be '{expected[index]}'.");
			}
		}

		SongTreeFolder oldSequences = (SongTreeFolder)oldRoot.Children[0];
		SongTreeFolder oldPatterns = (SongTreeFolder)oldRoot.Children[1];
		SongTreeFolder oldInstruments = (SongTreeFolder)oldRoot.Children[2];
		SongTreeFolder oldSamples = (SongTreeFolder)oldRoot.Children[3];

		SongTreeFolder envelopes = new("Envelopes");
		// Mirror only ancestor folders that contain Envelope nodes into the
		// new section. Mixed Instrument/Envelope folders keep their original
		// Instrument contents and folder organization. Never change ObjectIds,
		// references, or any audio/document revision when loading.
		ExtractLegacyEnvelopes(oldInstruments, envelopes);

		SongTreeFolder patches = new("Patches");
		patches.Children.AddRange(oldSamples.Children);
		SongTreeFolder migrated = new(oldRoot.Name);
		migrated.Children.Add(oldSequences);
		migrated.Children.Add(oldPatterns);
		migrated.Children.Add(patches);
		migrated.Children.Add(envelopes);
		migrated.Children.Add(oldInstruments);
		return migrated;
	}

	/// <summary>Promotes the exact five-root version-1 layout. Existing
	/// folders and object/tombstone placements are reused, not reclassified.</summary>
	private static SongTreeFolder UpgradeLegacyFiveSectionTree(
		SongTreeFolder oldRoot)
	{
		string[] expected =
			["Sequences", "Patterns", "Patches", "Envelopes", "Instruments"];
		for (int index = 0; index < expected.Length; index++)
		{
			if (oldRoot.Children[index] is not SongTreeFolder section
				|| !string.Equals(section.Name, expected[index],
					StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					$"Legacy five-root section {index} must be '{expected[index]}'.");
			}
		}

		SongTreeFolder upgraded = new(oldRoot.Name);
		upgraded.Children.Add(oldRoot.Children[0]); // Sequences
		upgraded.Children.Add(oldRoot.Children[1]); // Patterns
		upgraded.Children.Add(oldRoot.Children[2]); // Patches
		upgraded.Children.Add(new SongTreeFolder("Filters"));
		upgraded.Children.Add(oldRoot.Children[3]); // Envelopes
		upgraded.Children.Add(oldRoot.Children[4]); // Instruments
		return upgraded;
	}

	private void ExtractLegacyEnvelopes(
		SongTreeFolder instruments,
		SongTreeFolder envelopes)
	{
		for (int index = 0; index < instruments.Children.Count;)
		{
			SongTreeNode node = instruments.Children[index];
			if (node is SongTreeObject reference
				&& IsEnvelopeTreeReference(reference.ObjectId))
			{
				instruments.Children.RemoveAt(index);
				envelopes.Children.Add(node);
				continue;
			}

			if (node is SongTreeFolder folder)
			{
				SongTreeFolder envelopeFolder = new(folder.Name);
				ExtractLegacyEnvelopes(folder, envelopeFolder);
				if (envelopeFolder.Children.Count != 0)
					envelopes.Children.Add(envelopeFolder);
			}
			index++;
		}
	}

	private bool IsEnvelopeTreeReference(ObjectId id)
		=> (_objects.TryGetValue(id, out SongObject? obj)
				&& obj.Kind == SongObjectKind.Envelope)
			|| (_tombstones.TryGetValue(id, out ObjectTombstone? tombstone)
				&& tombstone.Kind == SongObjectKind.Envelope);

	internal void RestoreNextObjectId(uint nextObjectId)
	{
		if (nextObjectId != 0)
		{
			foreach (ObjectId id in _objects.Keys)
			{
				if (id.Value >= nextObjectId)
					throw new ArgumentOutOfRangeException(nameof(nextObjectId));
			}
			foreach (ObjectId id in _tombstones.Keys)
			{
				if (id.Value >= nextObjectId)
					throw new ArgumentOutOfRangeException(nameof(nextObjectId));
			}
		}

		_nextObjectId = nextObjectId;
	}

	private static void RemoveTreePlacements(
		SongTreeFolder folder,
		ObjectId objectId)
	{
		for (int index = folder.Children.Count - 1; index >= 0; index--)
		{
			SongTreeNode child = folder.Children[index];
			if (child is SongTreeObject songObject
				&& songObject.ObjectId == objectId)
			{
				folder.Children.RemoveAt(index);
			}
			else if (child is SongTreeFolder childFolder)
			{
				RemoveTreePlacements(childFolder, objectId);
			}
		}
	}

	private void EnsureNextIdPast(ObjectId id)
	{
		if (id.Value >= _nextObjectId)
		{
			if (id.Value == uint.MaxValue)
				_nextObjectId = 0;
			else
				_nextObjectId = id.Value + 1;
		}
	}
}
