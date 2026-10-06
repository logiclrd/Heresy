using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.ViewModels;

/// <summary>
/// UI projection of a song-tree node. Object nodes resolve their current
/// display name and kind through the immutable ObjectId; missing objects use
/// tombstone metadata and finally the raw ID.
/// </summary>
public sealed class SongTreeItemViewModel
{
	private SongTreeItemViewModel(
		SongTreeNode node,
		string displayName,
		SongObjectKind kind,
		ObjectId? objectId,
		bool isMissingReference,
		IReadOnlyList<SongTreeItemViewModel> children)
	{
		Node = node;
		DisplayName = displayName;
		Kind = kind;
		ObjectId = objectId;
		IsMissingReference = isMissingReference;
		Children = children;
	}

	public SongTreeNode Node { get; }

	public string DisplayName { get; }

	public SongObjectKind Kind { get; }

	public ObjectId? ObjectId { get; }

	public bool IsMissingReference { get; }

	public bool IsFolder => Node is SongTreeFolder;

	public IReadOnlyList<SongTreeItemViewModel> Children { get; }

	public static SongTreeItemViewModel Create(
		SongDocument document,
		SongTreeNode node)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(node);

		if (node is SongTreeFolder folder)
		{
			return new SongTreeItemViewModel(
				node,
				folder.Name,
				SongObjectKind.Unknown,
				null,
				isMissingReference: false,
				folder.Children
					.Select(child => Create(document, child))
					.ToArray());
		}

		SongTreeObject objectNode = (SongTreeObject)node;
		ObjectId id = objectNode.ObjectId;

		if (document.TryGet(id, out SongObject? songObject)
			&& songObject is not null)
		{
			return new SongTreeItemViewModel(
				node,
				songObject.Name,
				songObject.Kind,
				id,
				isMissingReference: false,
				Array.Empty<SongTreeItemViewModel>());
		}

		if (document.Tombstones.TryGetValue(
			id,
			out ObjectTombstone tombstone))
		{
			return new SongTreeItemViewModel(
				node,
				tombstone.LastKnownName,
				tombstone.Kind,
				id,
				isMissingReference: true,
				Array.Empty<SongTreeItemViewModel>());
		}

		return new SongTreeItemViewModel(
			node,
			$"<{id.Value}>",
			SongObjectKind.Unknown,
			id,
			isMissingReference: true,
			Array.Empty<SongTreeItemViewModel>());
	}
}
