using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent data-sequence authoring operations used by the
/// Avalonia arrangement editor.
/// </summary>
public static class SequenceDocumentEditor
{
	public static DataSequenceDefinition CreateDataSequence(
		DocumentWorkspace workspace,
		string name)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		ObjectId id = workspace.Document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(id, name.Trim());
		workspace.Document.Add(sequence, affectsAudio: true);
		return sequence;
	}

	public static void SetRootSequence(
		DocumentWorkspace workspace,
		SequenceDefinition sequence)
	{
		ValidateSequence(workspace, sequence);
		if (workspace.Document.RootSequenceId == sequence.Id)
			return;

		workspace.Document.RootSequenceId = sequence.Id;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void InsertEntry(
		DocumentWorkspace workspace,
		DataSequenceDefinition sequence,
		int index,
		ObjectId patternId,
		int startRow = 0)
	{
		ValidateSequence(workspace, sequence);
		ValidatePattern(workspace.Document, patternId);
		if ((uint)index > (uint)sequence.Entries.Count)
			throw new ArgumentOutOfRangeException(nameof(index));

		sequence.Entries.Insert(
			index,
			new SequenceEntry(patternId, startRow));
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void UpdateEntry(
		DocumentWorkspace workspace,
		DataSequenceDefinition sequence,
		int index,
		ObjectId patternId,
		int startRow)
	{
		ValidateSequence(workspace, sequence);
		ValidateEntryIndex(sequence, index);

		SequenceEntry current = sequence.Entries[index];
		if (patternId != current.PatternId)
			ValidatePattern(workspace.Document, patternId);

		SequenceEntry replacement =
			new(patternId, startRow);
		if (current == replacement)
			return;

		sequence.Entries[index] = replacement;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void RemoveEntry(
		DocumentWorkspace workspace,
		DataSequenceDefinition sequence,
		int index)
	{
		ValidateSequence(workspace, sequence);
		ValidateEntryIndex(sequence, index);

		sequence.Entries.RemoveAt(index);
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void MoveEntry(
		DocumentWorkspace workspace,
		DataSequenceDefinition sequence,
		int fromIndex,
		int toIndex)
	{
		ValidateSequence(workspace, sequence);
		ValidateEntryIndex(sequence, fromIndex);
		ValidateEntryIndex(sequence, toIndex);

		if (fromIndex == toIndex)
			return;

		SequenceEntry entry = sequence.Entries[fromIndex];
		sequence.Entries.RemoveAt(fromIndex);
		sequence.Entries.Insert(toIndex, entry);
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	private static void ValidateSequence(
		DocumentWorkspace workspace,
		SequenceDefinition sequence)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(sequence);

		if (!workspace.Document.TryGet(
			sequence.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, sequence))
		{
			throw new InvalidOperationException(
				"The sequence is not part of the active song document.");
		}
	}

	private static void ValidatePattern(
		SongDocument document,
		ObjectId patternId)
	{
		if (patternId.IsNone
			|| !document.TryGet(patternId, out SongObject? songObject)
			|| songObject is not PatternDefinition)
		{
			throw new InvalidOperationException(
				$"Object {patternId.Value} is not a live pattern in the active song.");
		}
	}

	private static void ValidateEntryIndex(
		DataSequenceDefinition sequence,
		int index)
	{
		if ((uint)index >= (uint)sequence.Entries.Count)
			throw new ArgumentOutOfRangeException(nameof(index));
	}
}
