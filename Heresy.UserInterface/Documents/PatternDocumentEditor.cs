using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent pattern authoring operations used by the Avalonia UI.
/// </summary>
public static class PatternDocumentEditor
{
	public static DataPatternDefinition CreateDataPattern(
		DocumentWorkspace workspace,
		string name,
		int rowCount = 64,
		int channelCount = 8)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidateLayout(rowCount, channelCount, 4, 16);

		ObjectId id = workspace.Document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(id, name.Trim())
			{
				RowCount = rowCount,
				ChannelCount = channelCount,
			};
		workspace.Document.Add(pattern, affectsAudio: true);
		return pattern;
	}

	public static void SetNote(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		int row,
		int channel,
		PatternNoteEntry? note)
	{
		ValidatePattern(workspace, pattern);
		PatternCell? cell = pattern.Grid[row, channel];

		if (Equals(cell?.Note, note))
			return;

		if (note is null)
		{
			if (cell is null)
				return;

			cell.Note = null;
			if (cell.IsEmpty)
				pattern.Grid.ClearCell(row, channel);
		}
		else
		{
			pattern.Grid.GetOrCreateCell(row, channel).Note = note;
		}

		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void UpdateLayout(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		int rowCount,
		int channelCount,
		int minorHighlightRows,
		int majorHighlightRows)
	{
		ValidatePattern(workspace, pattern);
		ValidateLayout(
			rowCount,
			channelCount,
			minorHighlightRows,
			majorHighlightRows);

		if (pattern.RowCount == rowCount
			&& pattern.ChannelCount == channelCount
			&& pattern.MinorHighlightRows == minorHighlightRows
			&& pattern.MajorHighlightRows == majorHighlightRows)
		{
			return;
		}

		pattern.RowCount = rowCount;
		pattern.ChannelCount = channelCount;
		pattern.MinorHighlightRows = minorHighlightRows;
		pattern.MajorHighlightRows = majorHighlightRows;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static bool WouldDiscardCells(
		DataPatternDefinition pattern,
		int rowCount,
		int channelCount)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (rowCount < 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));

		return pattern.Grid
			.EnumerateNonEmptyCells()
			.Any(cell =>
				cell.Row >= rowCount
					|| cell.Channel >= channelCount);
	}

	private static void ValidatePattern(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);

		if (!workspace.Document.TryGet(pattern.Id, out SongObject? existing)
			|| !ReferenceEquals(existing, pattern))
		{
			throw new InvalidOperationException(
				"The pattern is not part of the active song document.");
		}
	}

	private static void ValidateLayout(
		int rowCount,
		int channelCount,
		int minorHighlightRows,
		int majorHighlightRows)
	{
		if (rowCount < 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));
		if (minorHighlightRows < 0)
			throw new ArgumentOutOfRangeException(nameof(minorHighlightRows));
		if (majorHighlightRows < 0)
			throw new ArgumentOutOfRangeException(nameof(majorHighlightRows));
	}
}
