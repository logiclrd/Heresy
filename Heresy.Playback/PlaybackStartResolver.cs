using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;

namespace Heresy.Playback;

public abstract record PlaybackStartLocation;

public sealed record SequencePlaybackStartLocation(
	ObjectId SequenceId,
	int Order,
	int Row)
	: PlaybackStartLocation;

public sealed record PatternPlaybackStartLocation(
	ObjectId PatternId,
	int Row)
	: PlaybackStartLocation;

/// <summary>
/// Resolves F7-style "play from here" semantics. A known editor parent wins,
/// then the song root when it directly contains the pattern, then any other
/// data sequence which contains it. If no sequence parent can be established,
/// playback falls back to the standalone pattern at the current row.
/// </summary>
public static class PlaybackStartResolver
{
	public static PlaybackStartLocation ResolveFromPattern(
		SongDocument document,
		ObjectId patternId,
		int row,
		ObjectId? preferredSequenceId = null,
		int? preferredOrder = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (patternId.IsNone)
			throw new ArgumentException(
				"A playback pattern ID may not be zero.",
				nameof(patternId));
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));
		if (preferredOrder.HasValue
			&& preferredOrder.Value < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(preferredOrder));
		}

		if (preferredSequenceId.HasValue
			&& preferredOrder.HasValue
			&& TryMatch(
				document,
				preferredSequenceId.Value,
				patternId,
				preferredOrder.Value))
		{
			return new SequencePlaybackStartLocation(
				preferredSequenceId.Value,
				preferredOrder.Value,
				row);
		}

		if (!document.RootSequenceId.IsNone
			&& TryFindOrder(
				document,
				document.RootSequenceId,
				patternId,
				out int rootOrder))
		{
			return new SequencePlaybackStartLocation(
				document.RootSequenceId,
				rootOrder,
				row);
		}

		foreach (
			KeyValuePair<ObjectId, SongObject> pair
				in document.Objects.OrderBy(
					pair => pair.Key.Value))
		{
			if (pair.Value is not DataSequenceDefinition)
				continue;
			if (pair.Key == document.RootSequenceId)
				continue;

			if (TryFindOrder(
					document,
					pair.Key,
					patternId,
					out int order))
			{
				return new SequencePlaybackStartLocation(
					pair.Key,
					order,
					row);
			}
		}

		return new PatternPlaybackStartLocation(
			patternId,
			row);
	}

	private static bool TryMatch(
		SongDocument document,
		ObjectId sequenceId,
		ObjectId patternId,
		int order)
	{
		if (!document.TryGet(
				sequenceId,
				out SongObject? songObject)
			|| songObject is not DataSequenceDefinition sequence)
		{
			return false;
		}

		return (uint)order < (uint)sequence.Entries.Count
			&& sequence.Entries[order].PatternId == patternId;
	}

	private static bool TryFindOrder(
		SongDocument document,
		ObjectId sequenceId,
		ObjectId patternId,
		out int order)
	{
		order = -1;
		if (!document.TryGet(
				sequenceId,
				out SongObject? songObject)
			|| songObject is not DataSequenceDefinition sequence)
		{
			return false;
		}

		for (int index = 0;
			index < sequence.Entries.Count;
			index++)
		{
			if (sequence.Entries[index].PatternId == patternId)
			{
				order = index;
				return true;
			}
		}

		return false;
	}
}
