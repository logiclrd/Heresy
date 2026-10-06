using System;
using System.Collections.Generic;

using Heresy.Core.Instruments;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.Core.Objects;

public enum SongReferenceKind
{
	RootSequence,
	Tree,
	PatternNoteSource,
	SequencePattern,
	InstrumentSource,
	InstrumentVolumeEnvelope,
	InstrumentPitchEnvelope,
	InstrumentPanningEnvelope,
	InstrumentFilterEnvelope,
}

public sealed record SongReference(
	ObjectId TargetId,
	ObjectId? SourceObjectId,
	SongReferenceKind Kind);

public sealed record SongReferenceAnalysis(
	IReadOnlyList<SongReference> References,
	bool HasOpaqueScriptReferences);

/// <summary>
/// Discovers explicit ObjectId edges in the persistent song graph. Script
/// sources remain opaque until the restricted-C# compiler can report their
/// semantic object-reference tokens.
/// </summary>
public static class SongReferenceAnalyzer
{
	public static SongReferenceAnalysis Analyze(SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		List<SongReference> references = [];
		bool hasOpaqueScriptReferences = false;

		AddReference(
			references,
			document.RootSequenceId,
			null,
			SongReferenceKind.RootSequence);
		CollectTreeReferences(document.Root, references);

		foreach (SongObject songObject in document.Objects.Values)
		{
			switch (songObject)
			{
				case InstrumentDefinition instrument:
					foreach (ToneSpecification tone in instrument.ToneSpecifications)
					{
						AddReference(
							references,
							tone.SourceId,
							instrument.Id,
							SongReferenceKind.InstrumentSource);
						AddReference(
							references,
							tone.VolumeEnvelopeId,
							instrument.Id,
							SongReferenceKind.InstrumentVolumeEnvelope);
						AddReference(
							references,
							tone.PitchEnvelopeId,
							instrument.Id,
							SongReferenceKind.InstrumentPitchEnvelope);
						AddReference(
							references,
							tone.PanningEnvelopeId,
							instrument.Id,
							SongReferenceKind.InstrumentPanningEnvelope);
						AddReference(
							references,
							tone.FilterEnvelopeId,
							instrument.Id,
							SongReferenceKind.InstrumentFilterEnvelope);
					}
					break;

				case DataPatternDefinition pattern:
					foreach ((_, _, PatternCell cell) in pattern.Grid.EnumerateNonEmptyCells())
					{
						if (cell.Note is StartPatternNote start)
						{
							AddReference(
								references,
								start.SourceId,
								pattern.Id,
								SongReferenceKind.PatternNoteSource);
						}
					}
					break;

				case DataSequenceDefinition sequence:
					foreach (SequenceEntry entry in sequence.Entries)
					{
						AddReference(
							references,
							entry.PatternId,
							sequence.Id,
							SongReferenceKind.SequencePattern);
					}
					break;

				case ScriptPatternDefinition:
				case ScriptSequenceDefinition:
					hasOpaqueScriptReferences = true;
					break;
			}
		}

		return new SongReferenceAnalysis(
			references,
			hasOpaqueScriptReferences);
	}

	private static void CollectTreeReferences(
		SongTreeNode node,
		List<SongReference> references)
	{
		switch (node)
		{
			case SongTreeObject songObject:
				AddReference(
					references,
					songObject.ObjectId,
					null,
					SongReferenceKind.Tree);
				break;

			case SongTreeFolder folder:
				foreach (SongTreeNode child in folder.Children)
					CollectTreeReferences(child, references);
				break;
		}
	}

	private static void AddReference(
		List<SongReference> references,
		ObjectId targetId,
		ObjectId? sourceObjectId,
		SongReferenceKind kind)
	{
		if (!targetId.IsNone)
			references.Add(new SongReference(targetId, sourceObjectId, kind));
	}

	private static void AddReference(
		List<SongReference> references,
		ObjectId? targetId,
		ObjectId? sourceObjectId,
		SongReferenceKind kind)
	{
		if (targetId.HasValue)
			AddReference(references, targetId.Value, sourceObjectId, kind);
	}
}
