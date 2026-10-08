using System;
using System.Collections.Generic;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Executes sequence entries by invoking referenced patterns in order.
/// Tracker Bxx/Cxx control is consumed here rather than reaching playback.
/// </summary>
public static class SequenceNoteProcessor
{
	public static void GenerateNotes(
		DataSequenceDefinition sequence,
		ISequencePatternResolver resolver,
		SequencingContext context,
		INoteReceiver output,
		out TimeSpan duration)
	{
		ArgumentNullException.ThrowIfNull(sequence);

		GenerateNotes(
			sequence.Entries,
			resolver,
			context,
			output,
			out duration);
	}

	public static void GenerateNotes(
		IReadOnlyList<SequenceEntry> entries,
		ISequencePatternResolver resolver,
		SequencingContext context,
		INoteReceiver output,
		out TimeSpan duration)
		=> GenerateNotes(
			entries,
			resolver,
			context,
			output,
			startOrder: 0,
			startRow: null,
			out duration);

	public static void GenerateNotes(
		IReadOnlyList<SequenceEntry> entries,
		ISequencePatternResolver resolver,
		SequencingContext context,
		INoteReceiver output,
		int startOrder,
		int? startRow,
		out TimeSpan duration)
		=> GenerateNotes(
			entries,
			resolver,
			context,
			output,
			startOrder,
			startRow,
			out duration,
			rowStarted: null,
			shouldFollowOrderJump: null);

	public static void GenerateNotes(
		IReadOnlyList<SequenceEntry> entries,
		ISequencePatternResolver resolver,
		SequencingContext context,
		INoteReceiver output,
		int startOrder,
		int? startRow,
		out TimeSpan duration,
		Action<int, ObjectId, int, TimeSpan>? rowStarted,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);
		if (startOrder < 0)
			throw new ArgumentOutOfRangeException(nameof(startOrder));
		if (startRow.HasValue && startRow.Value < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		TimeSpan elapsed = TimeSpan.Zero;
		int order = startOrder;
		int? startRowOverride = startRow;
		int visits = 0;

		while ((uint)order < (uint)entries.Count)
		{
			if (++visits > NoteScheduleBuilder.MaximumGeneratedNotes)
			{
				throw new SequencingResourceLimitException(
					$"Sequence control exceeded {NoteScheduleBuilder.MaximumGeneratedNotes:N0} pattern visits.");
			}

			SequenceEntry entry = entries[order];
			int effectiveStartRow =
				startRowOverride ?? entry.StartRow;
			startRowOverride = null;

			if (!resolver.TryResolve(entry.PatternId, out IRawPatternNoteGenerator? pattern)
				|| pattern is null)
			{
				order++;
				continue;
			}

			NoteScheduleBuilder patternOutput = new();
			PatternNoteProcessor.GenerateNotes(
				pattern,
				context,
				patternOutput,
				effectiveStartRow,
				out TimeSpan patternDuration,
				out PatternFlowControl flowControl,
				rowStarted is null
					? null
					: (patternRow, patternOffset) =>
						rowStarted(
							order,
							entry.PatternId,
							patternRow,
							elapsed + patternOffset));

			foreach (NoteEvent noteEvent in patternOutput.Freeze())
			{
				output.Append(noteEvent with
				{
					Offset = new MusicalTime(
						elapsed + noteEvent.Offset.TimeOffset,
						noteEvent.Offset.RowOffset),
				});
			}

			elapsed += patternDuration;

			if (!flowControl.HasControl)
			{
				order++;
				continue;
			}

			if (flowControl.OrderJump.HasValue
				&& flowControl.SourceRow.HasValue
				&& shouldFollowOrderJump is not null)
			{
				SequenceOrderJumpEncounter encounter =
					new(
						entry.PatternId,
						flowControl.SourceRow.Value,
						flowControl.OrderJump.Value);
				if (!shouldFollowOrderJump(encounter))
					break;
			}

			order = flowControl.OrderJump ?? checked(order + 1);
			if (flowControl.BreakRow.HasValue)
				startRowOverride = flowControl.BreakRow.Value;
		}

		duration = elapsed;
	}
}
