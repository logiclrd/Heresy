using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Scripting;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptObjectReferenceOption(
	ObjectId Id,
	string DisplayName,
	SongObjectKind Kind)
{
	public string ReferenceText =>
		ScriptObjectReferenceSyntax.Format(Id);

	public override string ToString()
		=> $"{DisplayName} — {Kind} <{Id.Value}>";
}

/// <summary>
/// Framework-independent authoring operations for scripted pattern/sequence
/// definitions. Compilation remains outside Heresy.Core and outside this
/// editor layer.
/// </summary>
public static class ScriptDocumentEditor
{
	public static ScriptPatternDefinition CreateScriptPattern(
		DocumentWorkspace workspace,
		string name,
		int rowCount = 64,
		int channelCount = 8)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidatePatternLayout(
			rowCount,
			channelCount,
			minorHighlightRows: 4,
			majorHighlightRows: 16);

		ObjectId id = workspace.Document.AllocateObjectId();
		ScriptPatternDefinition pattern =
			new(id, name.Trim())
			{
				RowCount = rowCount,
				ChannelCount = channelCount,
			};
		workspace.Document.Add(pattern, affectsAudio: true);
		return pattern;
	}

	public static ScriptSequenceDefinition CreateScriptSequence(
		DocumentWorkspace workspace,
		string name)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		ObjectId id = workspace.Document.AllocateObjectId();
		ScriptSequenceDefinition sequence =
			new(id, name.Trim());
		workspace.Document.Add(sequence, affectsAudio: true);
		return sequence;
	}

	public static void UpdateSource(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		string source)
	{
		ValidateLive(workspace, pattern);
		ArgumentNullException.ThrowIfNull(source);
		if (pattern.Source == source)
			return;

		pattern.Source = source;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void UpdateSource(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		string source)
	{
		ValidateLive(workspace, sequence);
		ArgumentNullException.ThrowIfNull(source);
		if (sequence.Source == source)
			return;

		sequence.Source = source;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void UpdatePatternLayout(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		int rowCount,
		int channelCount,
		int minorHighlightRows,
		int majorHighlightRows)
	{
		ValidateLive(workspace, pattern);
		ValidatePatternLayout(
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

	public static void SetRootSequence(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence)
		=> SequenceDocumentEditor.SetRootSequence(
			workspace,
			sequence);

	public static ScriptObjectReferenceOption[] GetObjectReferences(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		return document.Objects.Values
			.OrderBy(songObject => songObject.Id.Value)
			.Select(songObject =>
				new ScriptObjectReferenceOption(
					songObject.Id,
					songObject.Name,
					songObject.Kind))
			.ToArray();
	}

	private static void ValidateLive(
		DocumentWorkspace workspace,
		SongObject scriptObject)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(scriptObject);

		if (!workspace.Document.TryGet(
			scriptObject.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, scriptObject))
		{
			throw new InvalidOperationException(
				"The script object is not part of the active song document.");
		}
	}

	private static void ValidatePatternLayout(
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
