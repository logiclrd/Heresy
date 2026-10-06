using System;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Mutates the organizational song tree without treating tree location as
/// ownership. Tree-only edits advance DocumentRevision, not AudioRevision.
/// </summary>
public static class SongTreeEditor
{
	public static SongTreeFolder CreateFolder(
		SongDocument document,
		SongTreeFolder parent,
		string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(parent);
		ValidateName(name);
		EnsureFolderAttached(document, parent);

		SongTreeFolder folder = new(name.Trim());
		parent.Children.Add(folder);
		document.MarkChanged(affectsAudio: false);
		return folder;
	}

	public static void RenameFolder(
		SongDocument document,
		SongTreeFolder folder,
		string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(folder);
		ValidateName(name);
		EnsureFolderAttached(document, folder);

		string normalizedName = name.Trim();
		if (string.Equals(folder.Name, normalizedName, StringComparison.Ordinal))
			return;

		folder.Name = normalizedName;
		document.MarkChanged(affectsAudio: false);
	}

	public static void RenameObject(
		SongDocument document,
		ObjectId objectId,
		string name)
	{
		ArgumentNullException.ThrowIfNull(document);
		ValidateName(name);

		if (!document.TryGet(objectId, out SongObject? songObject)
			|| songObject is null)
		{
			throw new InvalidOperationException(
				$"Object ID {objectId} does not exist in this song.");
		}

		string normalizedName = name.Trim();
		if (string.Equals(songObject.Name, normalizedName, StringComparison.Ordinal))
			return;

		songObject.Name = normalizedName;
		RenameTreePlacements(document.Root, objectId, normalizedName);
		document.MarkChanged(affectsAudio: false);
	}

	public static void DeleteObject(
		SongDocument document,
		ObjectId objectId)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (!document.Objects.ContainsKey(objectId))
		{
			throw new InvalidOperationException(
				$"Object ID {objectId} does not exist in this song.");
		}

		RemoveTreePlacements(document.Root, objectId);
		document.Remove(objectId, affectsAudio: true);
	}

	public static void RemoveNode(
		SongDocument document,
		SongTreeNode node)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);

		if (ReferenceEquals(node, document.Root))
			throw new InvalidOperationException("The song-tree root cannot be removed.");

		SongTreeFolder? parent = FindParent(document.Root, node);
		if (parent is null)
			throw new InvalidOperationException("The node is not attached to this song tree.");

		parent.Children.Remove(node);
		document.MarkChanged(affectsAudio: false);
	}

	public static SongTreeFolder? GetParent(
		SongDocument document,
		SongTreeNode node)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);

		return ReferenceEquals(node, document.Root)
			? null
			: FindParent(document.Root, node);
	}

	public static bool CanMoveInto(
		SongDocument document,
		SongTreeNode node,
		SongTreeFolder destination)
	{
		try
		{
			ValidateMove(document, node, destination);
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	public static bool CanMoveBefore(
		SongDocument document,
		SongTreeNode node,
		SongTreeNode target)
	{
		if (ReferenceEquals(node, target))
			return false;

		SongTreeFolder? targetParent = GetParent(document, target);
		return targetParent is not null
			&& CanMoveInto(document, node, targetParent);
	}

	public static void MoveInto(
		SongDocument document,
		SongTreeNode node,
		SongTreeFolder destination)
	{
		SongTreeFolder? source = GetParent(document, node);
		int insertionIndex = destination.Children.Count;
		if (ReferenceEquals(source, destination))
			insertionIndex--;

		Move(document, node, destination, insertionIndex);
	}

	public static void MoveBefore(
		SongDocument document,
		SongTreeNode node,
		SongTreeNode target)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(target);

		if (ReferenceEquals(node, target))
			return;

		SongTreeFolder? targetParent = GetParent(document, target);
		if (targetParent is null)
			throw new InvalidOperationException("The target node is not attached below the song-tree root.");

		int targetIndex = targetParent.Children.IndexOf(target);
		SongTreeFolder? source = GetParent(document, node);
		if (ReferenceEquals(source, targetParent))
		{
			int sourceIndex = targetParent.Children.IndexOf(node);
			if (sourceIndex < targetIndex)
				targetIndex--;
		}

		Move(document, node, targetParent, targetIndex);
	}

	public static void Move(
		SongDocument document,
		SongTreeNode node,
		SongTreeFolder destination,
		int insertionIndex)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(destination);
		ValidateMove(document, node, destination);

		SongTreeFolder source =
			FindParent(document.Root, node)
			?? throw new InvalidOperationException("The node is not attached to this song tree.");

		int sourceIndex = source.Children.IndexOf(node);
		int destinationCountAfterRemoval =
			ReferenceEquals(source, destination)
				? destination.Children.Count - 1
				: destination.Children.Count;

		if (insertionIndex < 0 || insertionIndex > destinationCountAfterRemoval)
			throw new ArgumentOutOfRangeException(nameof(insertionIndex));

		if (ReferenceEquals(source, destination)
			&& sourceIndex == insertionIndex)
		{
			return;
		}

		source.Children.RemoveAt(sourceIndex);
		destination.Children.Insert(insertionIndex, node);
		document.MarkChanged(affectsAudio: false);
	}

	private static void ValidateMove(
		SongDocument document,
		SongTreeNode node,
		SongTreeFolder destination)
	{
		if (ReferenceEquals(node, document.Root))
			throw new InvalidOperationException("The song-tree root cannot be moved.");

		if (FindParent(document.Root, node) is null)
			throw new InvalidOperationException("The node is not attached to this song tree.");

		EnsureFolderAttached(document, destination);

		if (node is SongTreeFolder folder
			&& Contains(folder, destination))
		{
			throw new InvalidOperationException(
				"A folder cannot be moved into itself or one of its descendants.");
		}
	}

	private static void EnsureFolderAttached(
		SongDocument document,
		SongTreeFolder folder)
	{
		if (!ReferenceEquals(folder, document.Root)
			&& FindParent(document.Root, folder) is null)
		{
			throw new InvalidOperationException(
				"The folder is not attached to this song tree.");
		}
	}

	private static void ValidateName(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
	}

	private static void RenameTreePlacements(
		SongTreeFolder folder,
		ObjectId objectId,
		string name)
	{
		foreach (SongTreeNode child in folder.Children)
		{
			switch (child)
			{
				case SongTreeObject songObject when songObject.ObjectId == objectId:
					songObject.Name = name;
					break;

				case SongTreeFolder childFolder:
					RenameTreePlacements(childFolder, objectId, name);
					break;
			}
		}
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

	private static SongTreeFolder? FindParent(
		SongTreeFolder parent,
		SongTreeNode node)
	{
		foreach (SongTreeNode child in parent.Children)
		{
			if (ReferenceEquals(child, node))
				return parent;

			if (child is SongTreeFolder folder)
			{
				SongTreeFolder? nested = FindParent(folder, node);
				if (nested is not null)
					return nested;
			}
		}

		return null;
	}

	private static bool Contains(
		SongTreeFolder root,
		SongTreeFolder candidate)
	{
		if (ReferenceEquals(root, candidate))
			return true;

		foreach (SongTreeNode child in root.Children)
		{
			if (child is SongTreeFolder folder
				&& Contains(folder, candidate))
			{
				return true;
			}
		}

		return false;
	}
}
