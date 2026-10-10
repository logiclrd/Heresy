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

	public void Add(SongObject songObject, bool affectsAudio = true)
	{
		ArgumentNullException.ThrowIfNull(songObject);

		SongTreeFolder sectionRoot =
			GetSectionRoot(SongTreeSections.ForKind(songObject.Kind));

		if (!_objects.TryAdd(songObject.Id, songObject))
			throw new InvalidOperationException($"Object ID {songObject.Id} is already in use.");

		_tombstones.Remove(songObject.Id);
		EnsureNextIdPast(songObject.Id);
		sectionRoot.Children.Add(
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

		// Older version-1 documents have four roots: Sequences, Patterns,
		// Instruments (also holding Envelopes), Samples. The five-root model
		// keeps the same format version and promotes Envelope placements.
		if (restoredRoot.Children.Count == 4)
			restoredRoot = UpgradeLegacyFourSectionTree(restoredRoot);

		if (restoredRoot.Children.Count != SongTreeSections.DocumentOrder.Length)
		{
			throw new InvalidOperationException(
				"A song tree must contain exactly the five fixed document sections.");
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
