using System;

namespace Heresy.Core.Objects;

/// <summary>
/// Persistent named object in a song. Containment in the UI tree does not imply
/// ownership: references between objects are always by <see cref="ObjectId"/>.
/// </summary>
public abstract class SongObject
{
	protected SongObject(ObjectId id, string name)
	{
		if (id.IsNone)
			throw new ArgumentException("Song objects may not use ObjectId.None.", nameof(id));

		Id = id;
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public ObjectId Id { get; }

	public string Name { get; set; }

	public abstract SongObjectKind Kind { get; }
}
