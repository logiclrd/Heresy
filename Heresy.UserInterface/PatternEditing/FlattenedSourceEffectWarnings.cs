using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Nonblocking editor warning driven by the same effect classifier used
/// by the coroutine renderer. It tracks deterministic Source selections and
/// the current logical note across rows of one data Pattern. Cross-Pattern
/// inheritance and dynamically selected Sources remain runtime-diagnosed.
/// </summary>
public static class FlattenedSourceEffectWarnings
{
	public static string? Describe(SongDocument document,
		DataPatternDefinition pattern, int row, int channel)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(pattern);
		PatternCell? cell = pattern.Grid[row, channel];
		if (cell is null || cell.Effects.Count == 0)
			return null;

		// A Source-column selection is remembered even without a note
		// start, but does not replace the currently sounding source.
		// Reconstruct only what this data Pattern establishes locally:
		// Sequence-order inheritance and script-selected Sources may be
		// unknowable statically and remain runtime-diagnosed.
		ObjectId rememberedSource = ObjectId.None;
		bool currentIsFlattened = false;
		for (int previous = 0; previous <= row; previous++)
		{
			PatternCell? earlier = pattern.Grid[previous, channel];
			if (earlier is null)
				continue;
			ObjectId selection = !earlier.SourceId.IsNone
				? earlier.SourceId
				: earlier.Note is StartPatternNote inline
					? inline.SourceId
					: ObjectId.None;
			if (!selection.IsNone)
				rememberedSource = selection;

			switch (earlier.Note)
			{
				case StartPatternNote start:
					currentIsFlattened = !start.Mixdown
						&& IsFlattenedSource(document, rememberedSource);
					break;
				case PatternNoteCut:
					// Cut detaches the instigator. Note Off intentionally
					// does not: release tails retain its live controller.
					currentIsFlattened = false;
					break;
			}
		}
		if (!currentIsFlattened)
			return null;

		string[] ignored = cell.Effects
			.Where(FlattenedSourceEffectPolicy.IsVoiceSpecific)
			.Select(e => e.GetType().Name.Replace("PatternEffect", ""))
			.ToArray();
		if (ignored.Length == 0)
			return null;
		bool hasCombinedSlide = cell.Effects.Any(e =>
			e is VibratoVolumeSlidePatternEffect
				or TonePortamentoVolumeSlidePatternEffect);
		return "Warning: these effects contain voice-specific operations "
			+ "which do not apply to the current non-mixdown flattened source: "
			+ string.Join(", ", ignored)
			+ ". "
			+ (hasCombinedSlide
				? "Kxx/Lxx retain their live source-note volume slides; "
					+ "only vibrato/portamento is ignored. "
				: "")
			+ "The effects remain stored and editable.";
	}
	private static bool IsFlattenedSource(SongDocument document, ObjectId source)
		=> !source.IsNone
			&& document.TryGet(source, out SongObject? definition)
			&& definition is PatternDefinition or SequenceDefinition;

}
