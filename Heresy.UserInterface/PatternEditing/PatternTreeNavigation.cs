using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Resolves adjacent tracker-editable pattern placements in the same depth-first
/// order as the Patterns tree.
/// </summary>
public static class PatternTreeNavigation
{
	public static SongTreeObject? FindAdjacent(
		SongDocument document,
		SongTreeObject current,
		int delta)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(current);
		if (delta is not -1 and not 1)
			throw new ArgumentOutOfRangeException(nameof(delta));

		List<SongTreeObject> patterns = [];
		CollectEditablePatterns(
			document,
			document.GetSectionRoot(SongTreeSection.Patterns),
			patterns);

		int index =
			patterns.FindIndex(
				node => ReferenceEquals(node, current));
		if (index < 0)
			return null;

		int next = index + delta;
		return (uint)next < (uint)patterns.Count
			? patterns[next]
			: null;
	}

	private static void CollectEditablePatterns(
		SongDocument document,
		SongTreeFolder folder,
		List<SongTreeObject> destination)
	{
		foreach (SongTreeNode child in folder.Children)
		{
			switch (child)
			{
				case SongTreeFolder nested:
					CollectEditablePatterns(
						document,
						nested,
						destination);
					break;

				case SongTreeObject node:
					if (document.TryGet(
							node.ObjectId,
							out SongObject? songObject)
						&& songObject is DataPatternDefinition)
					{
						destination.Add(node);
					}
					break;
			}
		}
	}
}
