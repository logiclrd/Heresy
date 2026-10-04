using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.Objects;

/// <summary>
/// Mutable authoring document. Playback layers are expected to take snapshots
/// or otherwise capture definitions at invocation boundaries.
/// </summary>
public sealed class SongDocument
{
	private readonly Dictionary<ObjectId, SongObject> _objects = [];
	private readonly Dictionary<ObjectId, ObjectTombstone> _tombstones = [];
	private uint _nextObjectId = 1;

	public const int FormatVersion = 1;

	public IReadOnlyDictionary<ObjectId, SongObject> Objects => _objects;
	public IReadOnlyDictionary<ObjectId, ObjectTombstone> Tombstones => _tombstones;

	public SongTreeFolder Root { get; } = new("Song");

	public ObjectId RootSequenceId { get; set; }

	public uint DocumentRevision { get; private set; }
	public uint AudioRevision { get; private set; }

	public ObjectId AllocateObjectId()
	{
		if (_nextObjectId == 0)
			throw new InvalidOperationException("The song has exhausted its object ID space.");

		return new ObjectId(_nextObjectId++);
	}

	public void Add(SongObject songObject, bool affectsAudio = true)
	{
		ArgumentNullException.ThrowIfNull(songObject);

		if (!_objects.TryAdd(songObject.Id, songObject))
			throw new InvalidOperationException($"Object ID {songObject.Id} is already in use.");

		_tombstones.Remove(songObject.Id);
		EnsureNextIdPast(songObject.Id);
		MarkChanged(affectsAudio);
	}

	public bool TryGet(ObjectId id, out SongObject? songObject)
		=> _objects.TryGetValue(id, out songObject);

	public bool Remove(ObjectId id, bool affectsAudio = true)
	{
		if (!_objects.Remove(id, out SongObject? removed))
			return false;

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
