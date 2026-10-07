using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// One editable tracker row as projected by the single pattern editor. In a
/// sequence context DisplayRow is deliberately separate from PatternRow so the
/// editor can flow across multiple live pattern objects without copying them.
/// </summary>
public sealed record PatternEditorRow(
	DataPatternDefinition Pattern,
	int PatternRow,
	int? SequenceEntryIndex,
	bool IsSegmentStart);

/// <summary>
/// Metadata for one pattern-editor segment. Sequence entries which cannot be
/// represented as data-pattern rows remain present as zero-row segments so the
/// UI can show the discontinuity rather than silently hiding it.
/// </summary>
public sealed record PatternEditorSegment(
	int? SequenceEntryIndex,
	ObjectId PatternId,
	string DisplayName,
	int StartRow,
	DataPatternDefinition? Pattern,
	int FirstDisplayRow,
	int DisplayRowCount,
	string? Status);

public sealed record PatternEditorPlaybackCursor(
	ObjectId PatternId,
	int PatternRow,
	ObjectId? SequenceId,
	int? SequenceEntryIndex);

/// <summary>
/// Framework-independent projection used by PatternEditorControl for both a
/// single pattern and a sequence of patterns. The projection never owns pattern
/// data: every row resolves back to the live DataPatternDefinition stored in the
/// SongDocument.
/// </summary>
public sealed class PatternEditorContext
{
	private readonly SongDocument _document;
	private readonly DataPatternDefinition? _singlePattern;
	private readonly DataSequenceDefinition? _sequence;
	private readonly int _initialEntryIndex;
	private readonly List<PatternEditorRow> _rows = [];
	private readonly List<PatternEditorSegment> _segments = [];

	private PatternEditorContext(
		SongDocument document,
		DataPatternDefinition pattern)
	{
		_document = document;
		_singlePattern = pattern;
		ValidateLiveObject(document, pattern);
		_initialEntryIndex = 0;
		Build();
	}

	private PatternEditorContext(
		SongDocument document,
		DataSequenceDefinition sequence,
		int initialEntryIndex)
	{
		_document = document;
		_sequence = sequence;
		ValidateLiveObject(document, sequence);
		if ((uint)initialEntryIndex >= (uint)sequence.Entries.Count)
			throw new ArgumentOutOfRangeException(nameof(initialEntryIndex));
		_initialEntryIndex = initialEntryIndex;
		Build();
	}

	public bool IsSequence => _sequence is not null;

	public string DisplayName =>
		_sequence?.Name
			?? _singlePattern!.Name;

	public IReadOnlyList<PatternEditorRow> Rows => _rows;

	public IReadOnlyList<PatternEditorSegment> Segments => _segments;

	public int InitialDisplayRow { get; private set; }

	public int MaxChannelCount
	{
		get
		{
			int maximum = 1;
			foreach (PatternEditorSegment segment in _segments)
			{
				if (segment.Pattern is not null)
					maximum = Math.Max(maximum, segment.Pattern.ChannelCount);
			}
			return maximum;
		}
	}

	public static PatternEditorContext ForPattern(
		SongDocument document,
		DataPatternDefinition pattern)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(pattern);
		return new PatternEditorContext(document, pattern);
	}

	public static PatternEditorContext ForSequence(
		SongDocument document,
		DataSequenceDefinition sequence,
		int initialEntryIndex)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(sequence);
		return new PatternEditorContext(
			document,
			sequence,
			initialEntryIndex);
	}

	public PatternEditorRow GetRow(int displayRow)
	{
		if ((uint)displayRow >= (uint)_rows.Count)
			throw new ArgumentOutOfRangeException(nameof(displayRow));
		return _rows[displayRow];
	}

	public int? FindAdjacentPatternDisplayRow(
		int currentDisplayRow,
		int delta)
	{
		if (delta is not -1 and not 1)
			throw new ArgumentOutOfRangeException(nameof(delta));

		PatternEditorRow current = GetRow(currentDisplayRow);
		if (current.SequenceEntryIndex is not int entryIndex)
			return null;

		int segmentIndex =
			_segments.FindIndex(
				segment => segment.SequenceEntryIndex == entryIndex);
		if (segmentIndex < 0)
			return null;

		for (
			int index = segmentIndex + delta;
			index >= 0 && index < _segments.Count;
			index += delta)
		{
			PatternEditorSegment segment = _segments[index];
			if (segment.DisplayRowCount > 0)
				return segment.FirstDisplayRow;
		}

		return null;
	}

	public PatternEditorPlaybackCursor GetPlaybackCursor(
		int displayRow)
	{
		PatternEditorRow row = GetRow(displayRow);
		return new PatternEditorPlaybackCursor(
			row.Pattern.Id,
			row.PatternRow,
			_sequence?.Id,
			row.SequenceEntryIndex);
	}

	public IEnumerable<int> FindDisplayRows(
		DataPatternDefinition pattern,
		int patternRow)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (patternRow < 0)
			throw new ArgumentOutOfRangeException(nameof(patternRow));

		for (int displayRow = 0; displayRow < _rows.Count; displayRow++)
		{
			PatternEditorRow row = _rows[displayRow];
			if (ReferenceEquals(row.Pattern, pattern)
				&& row.PatternRow == patternRow)
			{
				yield return displayRow;
			}
		}
	}

	/// <summary>
	/// Rebuilds the projection after a pattern layout mutation and returns the
	/// best display-row match for the previously focused logical occurrence.
	/// </summary>
	public int Refresh(PatternEditorRow? preferredRow = null)
	{
		Build();
		if (_rows.Count == 0)
			return 0;

		if (preferredRow is null)
			return InitialDisplayRow;

		for (int index = 0; index < _rows.Count; index++)
		{
			PatternEditorRow candidate = _rows[index];
			if (candidate.SequenceEntryIndex == preferredRow.SequenceEntryIndex
				&& ReferenceEquals(candidate.Pattern, preferredRow.Pattern)
				&& candidate.PatternRow == preferredRow.PatternRow)
			{
				return index;
			}
		}

		if (preferredRow.SequenceEntryIndex is int entryIndex)
		{
			PatternEditorSegment? segment =
				_segments.FirstOrDefault(item =>
					item.SequenceEntryIndex == entryIndex
						&& ReferenceEquals(item.Pattern, preferredRow.Pattern)
						&& item.DisplayRowCount > 0);
			if (segment is not null)
			{
				int desiredPatternRow =
					Math.Clamp(
						preferredRow.PatternRow,
						segment.StartRow,
						segment.StartRow + segment.DisplayRowCount - 1);
				return segment.FirstDisplayRow
					+ (desiredPatternRow - segment.StartRow);
			}
		}
		else if (_singlePattern is not null)
		{
			return Math.Clamp(
				preferredRow.PatternRow,
				0,
				_rows.Count - 1);
		}

		return InitialDisplayRow;
	}

	private void Build()
	{
		_rows.Clear();
		_segments.Clear();

		if (_singlePattern is not null)
		{
			ValidateLiveObject(_document, _singlePattern);
			int firstDisplayRow = _rows.Count;
			for (int row = 0; row < _singlePattern.RowCount; row++)
			{
				_rows.Add(
					new PatternEditorRow(
						_singlePattern,
						row,
						null,
						IsSegmentStart: row == 0));
			}
			_segments.Add(
				new PatternEditorSegment(
					null,
					_singlePattern.Id,
					_singlePattern.Name,
					0,
					_singlePattern,
					firstDisplayRow,
					_singlePattern.RowCount,
					null));
			InitialDisplayRow = 0;
			return;
		}

		DataSequenceDefinition sequence = _sequence!;
		ValidateLiveObject(_document, sequence);

		for (int entryIndex = 0; entryIndex < sequence.Entries.Count; entryIndex++)
		{
			SequenceEntry entry = sequence.Entries[entryIndex];
			int firstDisplayRow = _rows.Count;

			if (!_document.TryGet(entry.PatternId, out SongObject? songObject)
				|| songObject is null)
			{
				string missingName =
					_document.Tombstones.TryGetValue(
						entry.PatternId,
						out ObjectTombstone? tombstone)
						? tombstone.LastKnownName
						: $"Object <{entry.PatternId.Value}>";
				_segments.Add(
					new PatternEditorSegment(
						entryIndex,
						entry.PatternId,
						missingName,
						entry.StartRow,
						null,
						firstDisplayRow,
						0,
						$"missing pattern '{missingName}' <{entry.PatternId.Value}>"));
				continue;
			}

			if (songObject is not DataPatternDefinition pattern)
			{
				_segments.Add(
					new PatternEditorSegment(
						entryIndex,
						entry.PatternId,
						songObject.Name,
						entry.StartRow,
						null,
						firstDisplayRow,
						0,
						songObject is ScriptPatternDefinition
							? $"Script pattern '{songObject.Name}' is not editable in the tracker grid."
							: $"Pattern '{songObject.Name}' is not editable in the tracker grid."));
				continue;
			}

			int startRow =
				Math.Min(entry.StartRow, pattern.RowCount);
			int visibleRows =
				Math.Max(0, pattern.RowCount - startRow);
			for (int row = startRow; row < pattern.RowCount; row++)
			{
				_rows.Add(
					new PatternEditorRow(
						pattern,
						row,
						entryIndex,
						IsSegmentStart: row == startRow));
			}

			string? status =
				visibleRows == 0
					? $"Start row {entry.StartRow} is at or beyond the end of '{pattern.Name}'."
					: null;
			_segments.Add(
				new PatternEditorSegment(
					entryIndex,
					entry.PatternId,
					pattern.Name,
					entry.StartRow,
					pattern,
					firstDisplayRow,
					visibleRows,
					status));
		}

		InitialDisplayRow =
			FindInitialDisplayRow(_initialEntryIndex);
	}

	private int FindInitialDisplayRow(int entryIndex)
	{
		if (_rows.Count == 0)
			return 0;

		PatternEditorSegment? selected =
			_segments.FirstOrDefault(
				segment => segment.SequenceEntryIndex == entryIndex);
		if (selected is not null && selected.DisplayRowCount > 0)
			return selected.FirstDisplayRow;

		foreach (PatternEditorSegment segment in _segments)
		{
			if (segment.SequenceEntryIndex > entryIndex
				&& segment.DisplayRowCount > 0)
			{
				return segment.FirstDisplayRow;
			}
		}

		for (int index = _segments.Count - 1; index >= 0; index--)
		{
			PatternEditorSegment segment = _segments[index];
			if (segment.SequenceEntryIndex < entryIndex
				&& segment.DisplayRowCount > 0)
			{
				return segment.FirstDisplayRow
					+ segment.DisplayRowCount - 1;
			}
		}

		return 0;
	}

	private static void ValidateLiveObject(
		SongDocument document,
		SongObject songObject)
	{
		if (!document.TryGet(songObject.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, songObject))
		{
			throw new InvalidOperationException(
				"The editor context object is not part of the active song document.");
		}
	}
}
