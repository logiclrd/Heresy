using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.ViewModels;

public sealed class SequenceEntryViewModel
{
	private SequenceEntryViewModel(
		int index,
		ObjectId patternId,
		string patternText,
		int startRow,
		bool isMissing)
	{
		Index = index;
		PatternId = patternId;
		PatternText = patternText;
		StartRow = startRow;
		IsMissing = isMissing;
	}

	public int Index { get; }
	public ObjectId PatternId { get; }
	public string PatternText { get; }
	public int StartRow { get; }
	public bool IsMissing { get; }

	public static SequenceEntryViewModel Create(
		SongDocument document,
		DataSequenceDefinition sequence,
		int index)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(sequence);
		if ((uint)index >= (uint)sequence.Entries.Count)
			throw new ArgumentOutOfRangeException(nameof(index));

		SequenceEntry entry = sequence.Entries[index];

		if (document.TryGet(
			entry.PatternId,
			out SongObject? songObject))
		{
			if (songObject is PatternDefinition pattern)
			{
				return new SequenceEntryViewModel(
					index,
					entry.PatternId,
					$"{pattern.Name} <{entry.PatternId.Value}>",
					entry.StartRow,
					false);
			}

			return new SequenceEntryViewModel(
				index,
				entry.PatternId,
				$"⚠ {songObject!.Name} <{entry.PatternId.Value}> (not a pattern)",
				entry.StartRow,
				true);
		}

		if (document.Tombstones.TryGetValue(
			entry.PatternId,
			out ObjectTombstone tombstone))
		{
			return new SequenceEntryViewModel(
				index,
				entry.PatternId,
				$"⚠ {tombstone.LastKnownName} <{entry.PatternId.Value}>",
				entry.StartRow,
				true);
		}

		return new SequenceEntryViewModel(
			index,
			entry.PatternId,
			$"⚠ <{entry.PatternId.Value}>",
			entry.StartRow,
			true);
	}
}
