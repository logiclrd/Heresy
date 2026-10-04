using System.Collections.Generic;

namespace Heresy.Core.Objects;

/// <summary>
/// Organizational tree node. The tree is UI organization only; it is not the
/// ownership graph of song objects.
/// </summary>
public abstract class SongTreeNode
{
	protected SongTreeNode(string name) => Name = name;

	public string Name { get; set; }
}

public sealed class SongTreeFolder : SongTreeNode
{
	public SongTreeFolder(string name) : base(name) { }

	public List<SongTreeNode> Children { get; } = [];
}

public sealed class SongTreeObject : SongTreeNode
{
	public SongTreeObject(string displayName, ObjectId objectId) : base(displayName)
		=> ObjectId = objectId;

	public ObjectId ObjectId { get; }
}
