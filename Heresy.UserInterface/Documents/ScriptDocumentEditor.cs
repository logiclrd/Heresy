using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Scripting;
using Heresy.Core.Sequences;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptReferenceInsertion(
	string Text,
	int Caret);

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
		int channelCount = 8,
		SongTreeFolder? destinationFolder = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidatePatternLayout(
			rowCount,
			channelCount,
			minorHighlightRows: 4,
			majorHighlightRows: 16);

		workspace.Document.ResolveObjectDestination(
			SongTreeSection.Patterns, destinationFolder);
		ObjectId id = workspace.Document.AllocateObjectId();
		ScriptPatternDefinition pattern =
			new(id, name.Trim())
			{
				RowCount = rowCount,
				ChannelCount = channelCount,
				Source = CreateInitialPatternSource(workspace.Document),
			};
		workspace.Document.Add(pattern, affectsAudio: true,
			destinationFolder: destinationFolder);
		return pattern;
	}

	public static ScriptSequenceDefinition CreateScriptSequence(
		DocumentWorkspace workspace,
		string name,
		SongTreeFolder? destinationFolder = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		workspace.Document.ResolveObjectDestination(
			SongTreeSection.Sequences, destinationFolder);
		ObjectId id = workspace.Document.AllocateObjectId();
		ScriptSequenceDefinition sequence =
			new(id, name.Trim())
			{
				Source = CreateInitialSequenceSource(workspace.Document),
			};
		workspace.Document.Add(sequence, affectsAudio: true,
			destinationFolder: destinationFolder);
		return sequence;
	}

	/// <summary>Initial authored source, generated once before the new
	/// definition is published. Reopening a script never regenerates it.</summary>
	public static string CreateInitialPatternSource(SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		PatternSourceOption? source = PatternSourceCatalog.GetSources(document)
			.FirstOrDefault();
		return source is null
			? "// Add a Sample, Synth or Instrument, then use Note(row, channel, _O(id), pitchMultiplier: 1.0);"
			: $"Note(0, 0, _O({source.Id.Value}), pitchMultiplier: 1.0, volume: 0.75);";
	}

	/// <summary>Prefer a real Pattern reference; Play requires a Pattern,
	/// not a Sequence or arbitrary sound source. No Pattern means silence.</summary>
	public static string CreateInitialSequenceSource(SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		PatternDefinition? pattern = document.Objects.Values
			.OfType<PatternDefinition>()
			.OrderBy(item => item.Id.Value)
			.FirstOrDefault();
		return pattern is null
			? "return default; // No Patterns exist yet."
			: $"switch (sequenceIndex)\n{{\n    case 0: return Play(_O({pattern.Id.Value}));\n    default: return default;\n}}";
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

	public static ScriptReferenceInsertion InsertObjectReference(
		string source,
		int selectionStart,
		int selectionEnd,
		ObjectId id)
	{
		ArgumentNullException.ThrowIfNull(source);

		int first =
			Math.Clamp(
				Math.Min(selectionStart, selectionEnd),
				0,
				source.Length);
		int last =
			Math.Clamp(
				Math.Max(selectionStart, selectionEnd),
				first,
				source.Length);
		string insertion =
			ScriptObjectReferenceSyntax.Format(id);

		return new ScriptReferenceInsertion(
			source[..first]
				+ insertion
				+ source[last..],
			first + insertion.Length);
	}

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
