using System;

using Heresy.Core.Diagnostics;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Executes a data-driven sequence by invoking referenced patterns in order.
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
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);

		TimeSpan elapsed = TimeSpan.Zero;
		int order = 0;
		int? startRowOverride = null;
		int visits = 0;

		while ((uint)order < (uint)sequence.Entries.Count)
		{
			if (++visits > NoteScheduleBuilder.MaximumGeneratedNotes)
			{
				throw new SequencingResourceLimitException(
					$"Sequence control exceeded {NoteScheduleBuilder.MaximumGeneratedNotes:N0} pattern visits.");
			}

			SequenceEntry entry = sequence.Entries[order];
			int startRow = startRowOverride ?? entry.StartRow;
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
				startRow,
				out TimeSpan patternDuration,
				out PatternFlowControl flowControl);

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

			order = flowControl.OrderJump ?? checked(order + 1);
			if (flowControl.BreakRow.HasValue)
				startRowOverride = flowControl.BreakRow.Value;
		}

		duration = elapsed;
	}
}
