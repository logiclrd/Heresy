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
/// <summary>Editor-only analysis, never a claim that arbitrary scripts or
/// flow changes are statically resolved. Lifecycle notices do not imply
/// effects are invalid or that stored notes have been modified.</summary>
public sealed record FlattenedSourceEditorIndication(
	string Message, bool IsConditional, bool HasVoiceSpecificEffects,
	bool IsLifecycle);

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
	/// <summary>
	/// Analyze each displayed Sequence order occurrence independently.
	/// A Pattern reused twice may have different inherited note state,
	/// and a scripted/missing order or Bxx/Cxx invalidates the deterministic
	/// linear projection. All uncertainty is advisory, never an assertion
	/// that an effect definitely becomes ineffective at runtime.
	///
	/// This is computed once per grid refresh, not once per rendered cell.
	/// Skipped StartRow prefixes and zero-row script segments are respected.
	/// </summary>
	public static IReadOnlyDictionary<(int Row, int Channel),
		FlattenedSourceEditorIndication> Analyze(
			SongDocument document, PatternEditorContext context)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(context);
		Dictionary<(int Row, int Channel),
			FlattenedSourceEditorIndication> results = [];
		AnalysisChannel[] channels = new AnalysisChannel[context.MaxChannelCount];
		for (int channel = 0; channel < channels.Length; channel++)
		{
			channels[channel] = new AnalysisChannel();
			if (context.IsSequence)
				channels[channel].MarkUnknown(
					"the Sequence may inherit a Source or logical note from its caller");
		}

		foreach (PatternEditorSegment segment in context.Segments)
		{
			// Script-driven Patterns can select Sources or issue lifecycle
			// actions at arbitrary times. Missing entries are not safely
			// predictable either; do not pretend they preserve state.
			if (segment.Pattern is null)
			{
				string reason = segment.Status?.Contains("Script",
					StringComparison.OrdinalIgnoreCase) == true
					? "an earlier script may dynamically select or replace the Source"
					: "an earlier unresolved Sequence entry may change the Source";
				foreach (AnalysisChannel channel in channels)
					channel.MarkUnknown(reason);
				continue;
			}
			for (int display = segment.FirstDisplayRow;
				display < segment.FirstDisplayRow + segment.DisplayRowCount;
				display++)
			{
				PatternEditorRow row = context.GetRow(display);
				bool flowTransfer = false;
				for (int channelId = 0;
					channelId < row.Pattern.ChannelCount; channelId++)
				{
					PatternCell? cell = row.Pattern.Grid[row.PatternRow, channelId];
					if (cell is null)
						continue;
					AnalysisChannel channel = channels[channelId];
					SourceKnowledge before = channel.Current;
					int? inheritedOrder = channel.StartOrder;

					ObjectId selection = !cell.SourceId.IsNone
						? cell.SourceId
						: cell.Note is StartPatternNote inline
							? inline.SourceId
							: ObjectId.None;
					if (!selection.IsNone)
					{
						channel.Remembered = Classify(document, selection);
						channel.SourceSelectionOrder = segment.SequenceEntryIndex;
						channel.Reason = channel.Remembered == SourceKnowledge.Unknown
							? "the selected Source is unresolved"
							: null;
					}

					switch (cell.Note)
					{
						case StartPatternNote start:
							channel.Current = start.Mixdown
								? SourceKnowledge.Ordinary
								: channel.Remembered;
							channel.Released = false;
							channel.StartOrder = segment.SequenceEntryIndex;
							break;
						case PatternNoteOff:
							// Off stops the producer, but descendants can
							// remain audible through their release envelopes.
							if (channel.Current is SourceKnowledge.Flattened
								or SourceKnowledge.Unknown)
								channel.Released = true;
							break;
						case PatternNoteCut:
							channel.Current = SourceKnowledge.None;
							channel.Released = false;
							channel.StartOrder = null;
							break;
					}
					SourceKnowledge current = channel.Current;
					bool lifecycle = (cell.Note is PatternNoteOff or PatternNoteCut)
						&& (before is SourceKnowledge.Flattened
							or SourceKnowledge.Unknown);

					string[] incompatible = cell.Effects
						.Where(FlattenedSourceEffectPolicy.IsVoiceSpecific)
						.Select(effect => effect.GetType().Name.Replace(
							"PatternEffect", ""))
						.ToArray();
					bool warns = incompatible.Length != 0
						&& (current is SourceKnowledge.Flattened
							or SourceKnowledge.Unknown);

					if (lifecycle || warns)
					{
						bool conditional = warns
							? current == SourceKnowledge.Unknown
							: before == SourceKnowledge.Unknown;
						string? inherited = context.IsSequence
							&& (warns ? channel.StartOrder : inheritedOrder)
								is int started
							&& segment.SequenceEntryIndex != started
								? $" The flattened source was inherited from sequence order {started}."
								: null;
						string? inheritedSelection = context.IsSequence
							&& channel.Current == SourceKnowledge.Flattened
							&& channel.SourceSelectionOrder is int selectedOrder
							&& segment.SequenceEntryIndex != selectedOrder
							&& channel.StartOrder == segment.SequenceEntryIndex
								? $" The remembered Source selection was inherited from sequence order {selectedOrder}."
								: null;
						string lifecycleMessage = cell.Note switch
						{
							PatternNoteOff when lifecycle && !conditional =>
								"Note Off ends future notes from the flattened source and releases existing descendants; its live source-volume controller can still affect releasing tails.",
							PatternNoteCut when lifecycle && !conditional =>
								"Note Cut terminates the flattened source and its descendant voices; later rows no longer address that logical note.",
							PatternNoteOff when lifecycle =>
								"Note Off may release an inherited or dynamically selected flattened source, if one is active.",
							PatternNoteCut when lifecycle =>
								"Note Cut may terminate an inherited or dynamically selected flattened source, if one is active.",
							_ => string.Empty,
						};
						string effectMessage = warns
							? (conditional
								? "Possible warning: voice-specific effects may not apply if the inherited or dynamically selected Source is a non-mixdown flattened Pattern/Sequence: "
								: "Warning: these effects contain voice-specific operations which do not apply to the current "
									+ (channel.Released ? "releasing" : "active")
									+ " non-mixdown flattened source: ")
								+ string.Join(", ", incompatible) + "."
							: string.Empty;
						bool combined = warns && cell.Effects.Any(effect =>
							effect is VibratoVolumeSlidePatternEffect
								or TonePortamentoVolumeSlidePatternEffect);
						string details = combined
							? " Kxx/Lxx retain their live source-note volume slides; only vibrato/portamento is ignored."
							: string.Empty;
						string uncertainty = conditional && channel.Reason is not null
							? " This is conditional because " + channel.Reason + "."
							: string.Empty;
						results[(display, channelId)] = new(
							(string.IsNullOrEmpty(lifecycleMessage)
								? effectMessage
								: string.IsNullOrEmpty(effectMessage)
									? lifecycleMessage
									: lifecycleMessage + " " + effectMessage)
								+ inherited + inheritedSelection + uncertainty + details
								+ " Stored effects remain editable.",
							conditional, warns, lifecycle);
					}

					if (cell.Effects.Any(effect =>
						effect is TrackerOrderJumpPatternEffect
							or TrackerPatternBreakPatternEffect))
						flowTransfer = true;
				}
				if (flowTransfer)
				{
					// Bxx/Cxx can skip, repeat or change a subsequent
					// entry's starting row. A linear visual pass can no
					// longer assert which Source will be active afterward.
					foreach (AnalysisChannel channel in channels)
						channel.MarkUnknown(
							"an earlier Bxx/Cxx flow command can change the actual order or start row");
				}
			}
		}
		return results;
	}

	private enum SourceKnowledge
	{
		None,
		Ordinary,
		Flattened,
		Unknown,
	}

	private sealed class AnalysisChannel
	{
		public SourceKnowledge Remembered { get; set; }
		public SourceKnowledge Current { get; set; }
		public bool Released { get; set; }
		public int? StartOrder { get; set; }
		public int? SourceSelectionOrder { get; set; }
		public string? Reason { get; set; }

		public void MarkUnknown(string reason)
		{
			Remembered = SourceKnowledge.Unknown;
			Current = SourceKnowledge.Unknown;
			Released = false;
			StartOrder = null;
			SourceSelectionOrder = null;
			Reason = reason;
		}
	}

	private static SourceKnowledge Classify(SongDocument document, ObjectId source)
	{
		if (source.IsNone)
			return SourceKnowledge.None;
		if (!document.TryGet(source, out SongObject? definition))
			return SourceKnowledge.Unknown;
		return definition is PatternDefinition or SequenceDefinition
			? SourceKnowledge.Flattened
			: SourceKnowledge.Ordinary;
	}

	private static bool IsFlattenedSource(SongDocument document, ObjectId source)
		=> !source.IsNone
			&& document.TryGet(source, out SongObject? definition)
			&& definition is PatternDefinition or SequenceDefinition;

}
