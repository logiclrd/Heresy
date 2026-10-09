using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Nonblocking editor warning driven by the same effect classifier used
/// by the coroutine renderer. An omitted source can be resolved from an
/// earlier cell in the same Pattern; cross-Pattern inherited sources are
/// diagnosed authoritatively during playback.
/// </summary>
public static class FlattenedSourceEffectWarnings
{
	public static string? Describe(SongDocument document,
		DataPatternDefinition pattern, int row, int channel)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(pattern);
		PatternCell? cell = pattern.Grid[row, channel];
		if (cell?.Note is not StartPatternNote { Mixdown: false } start)
			return null;
		ObjectId source = !cell.SourceId.IsNone
			? cell.SourceId : start.SourceId;
		if (source.IsNone)
		{
			for (int previous = row - 1; previous >= 0; previous--)
			{
				PatternCell? earlier = pattern.Grid[previous, channel];
				if (earlier is null)
					continue;
				if (!earlier.SourceId.IsNone)
					source = earlier.SourceId;
				else if (earlier.Note is StartPatternNote note
					&& !note.SourceId.IsNone)
					source = note.SourceId;
				if (!source.IsNone)
					break;
			}
		}
		if (source.IsNone
			|| !document.TryGet(source, out SongObject? definition)
			|| definition is not (PatternDefinition or SequenceDefinition))
			return null;

		string[] ignored = cell.Effects
			.Where(FlattenedSourceEffectPolicy.IsVoiceSpecific)
			.Select(e => e.GetType().Name.Replace("PatternEffect", ""))
			.ToArray();
		if (ignored.Length == 0)
			return null;
		return "Warning: these effects require a single voice and will be "
			+ "ignored on this non-mixdown flattened source: "
			+ string.Join(", ", ignored)
			+ ". They remain stored and editable.";
	}
}
