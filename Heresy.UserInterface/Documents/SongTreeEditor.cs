using System;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Mutates the organizational song tree without treating tree location as
/// ownership. Tree-only edits advance DocumentRevision, not AudioRevision.
/// </summary>
public static class SongTreeEditor
{
	public static void Move(
		SongDocument document,
		SongTreeNode node,
		SongTreeFolder destination,
		int insertionIndex)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);
		ArgumentNullException.ThrowIfNull(destination);

		if (ReferenceEquals(node, document.Root))
			throw new InvalidOperationException("The song-tree root cannot be moved.");

		SongTreeFolder? source = FindParent(document.Root, node);
		if (source is null)
			throw new InvalidOperationException("The node is not attached to this song tree.");

		if (!ReferenceEquals(destination, document.Root)
			&& FindParent(document.Root, destination) is null)
		{
			throw new InvalidOperationException(
				"The destination folder is not attached to this song tree.");
		}

		if (node is SongTreeFolder folder
			&& Contains(folder, destination))
		{
			throw new InvalidOperationException(
				"A folder cannot be moved into itself or one of its descendants.");
		}

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
