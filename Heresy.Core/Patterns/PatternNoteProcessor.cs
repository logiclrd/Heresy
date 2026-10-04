using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Patterns;

/// <summary>
/// Common second stage for every pattern implementation. A data grid or script
/// first emits raw note events in row/time coordinates; this processor resolves
/// those coordinates against the current sequencing state and emits a stable,
/// wall-time schedule.
/// </summary>
public static class PatternNoteProcessor
{
	private sealed class WorkingEvent
	{
		public required NoteEvent NoteEvent { get; init; }
		public required double RowOffset { get; init; }
		public required double TimeOffsetSeconds { get; init; }
		public required bool AffectsTiming { get; init; }
		public double? TimingEligibilitySeconds { get; set; }
	}

	/// <summary>
	/// Generates and resolves one complete pattern invocation. <paramref name="startRow"/>
	/// skips rows without executing their events. Timing in the skipped region is
	/// calculated using the sequencing state in effect on entry.
	/// </summary>
	public static void GenerateNotes(
		IRawPatternNoteGenerator generator,
		SequencingContext context,
		INoteReceiver output,
		int startRow,
		out TimeSpan duration)
	{
		ArgumentNullException.ThrowIfNull(generator);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);

		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		NoteScheduleBuilder rawBuilder = new();
		generator.GenerateRawNotes(context, rawBuilder, out double rowCount);
		NoteSchedule rawSchedule = rawBuilder.Freeze();

		if (double.IsNaN(rowCount) || double.IsInfinity(rowCount) || rowCount < 0.0)
			throw new InvalidOperationException("A pattern generator returned an invalid row count.");

		double entryRowDurationSeconds = GetRowDurationSeconds(context.State);
		double skippedRows = Math.Min(startRow, rowCount);
		double skippedTimeSeconds = skippedRows * entryRowDurationSeconds;
		double effectiveRowCount = Math.Max(0.0, rowCount - startRow);

		List<WorkingEvent> events = new(rawSchedule.Count);
		foreach (NoteEvent noteEvent in rawSchedule)
		{
			double sourceRowOffset = noteEvent.Offset.RowOffset;
			double rowsCollapsedBySkip = Math.Min(Math.Max(sourceRowOffset, 0.0), skippedRows);
			double adjustedTimeSeconds =
				noteEvent.Offset.TimeOffset.TotalSeconds
				+ rowsCollapsedBySkip * entryRowDurationSeconds
				- skippedTimeSeconds;

			double adjustedRowOffset = Math.Max(0.0, sourceRowOffset - skippedRows);

			// Events which have been moved wholly before the new origin are not
			// executed. Remaining row distance is intentionally not part of this
			// test: StartRow first collapses the skipped portion into wall time.
			if (adjustedTimeSeconds < 0.0 && adjustedRowOffset <= 0.0)
				continue;

			events.Add(new WorkingEvent
			{
				NoteEvent = noteEvent,
				RowOffset = adjustedRowOffset,
				TimeOffsetSeconds = adjustedTimeSeconds,
				AffectsTiming = AffectsTiming(noteEvent),
			});
		}

		List<NoteEvent> resolved = new(events.Count);
		List<WorkingEvent> deferredTimingEvents = [];

		double rowStartSeconds = 0.0;
		int wholeRowCount = (int)Math.Ceiling(effectiveRowCount);

		for (int row = 0; row < wholeRowCount; row++)
		{
			// Timing events may only take effect at row boundaries. The fractional
			// portion of their RowOffset is ignored. A non-zero fixed time offset
			// can defer them to a later boundary.
			foreach (WorkingEvent workingEvent in events)
			{
				if (!workingEvent.AffectsTiming || workingEvent.TimingEligibilitySeconds.HasValue)
					continue;

				int nominalRow = FloorRow(workingEvent.RowOffset);
				if (nominalRow != row)
					continue;

				workingEvent.TimingEligibilitySeconds = rowStartSeconds + workingEvent.TimeOffsetSeconds;
				deferredTimingEvents.Add(workingEvent);
			}

			List<WorkingEvent> dueTimingEvents = [];
			foreach (WorkingEvent workingEvent in deferredTimingEvents)
			{
				if (workingEvent.TimingEligibilitySeconds!.Value <= rowStartSeconds)
					dueTimingEvents.Add(workingEvent);
			}

			dueTimingEvents.Sort(CompareTimingEvents);
			foreach (WorkingEvent workingEvent in dueTimingEvents)
			{
				ApplyTimingCommands(context.State, workingEvent.NoteEvent.Commands);
				resolved.Add(ResolveAt(workingEvent.NoteEvent, rowStartSeconds, context));
				deferredTimingEvents.Remove(workingEvent);
			}

			double rowDurationSeconds = GetRowDurationSeconds(context.State);
			double rowEnd = Math.Min(row + 1.0, effectiveRowCount);
			double rowFraction = rowEnd - row;

			foreach (WorkingEvent workingEvent in events)
			{
				if (workingEvent.AffectsTiming)
					continue;

				if (workingEvent.RowOffset < row || workingEvent.RowOffset >= row + 1.0)
					continue;

				double fraction = workingEvent.RowOffset - row;
				if (fraction > rowFraction)
					continue;

				double eventTimeSeconds =
					rowStartSeconds
					+ fraction * rowDurationSeconds
					+ workingEvent.TimeOffsetSeconds;

				resolved.Add(ResolveAt(workingEvent.NoteEvent, eventTimeSeconds, context));
			}

			rowStartSeconds += rowDurationSeconds * rowFraction;
		}

		duration = TimeSpan.FromSeconds(rowStartSeconds);

		resolved.Sort(CompareResolvedEvents);
		foreach (NoteEvent noteEvent in resolved)
			output.Append(noteEvent);
	}

	public static void GenerateNotes(
		IRawPatternNoteGenerator generator,
		SequencingContext context,
		INoteReceiver output,
		out TimeSpan duration)
		=> GenerateNotes(generator, context, output, 0, out duration);

	private static bool AffectsTiming(NoteEvent noteEvent)
	{
		foreach (NoteCommand command in noteEvent.Commands)
		{
			if (command is SetTempoCommand or SetSpeedCommand)
				return true;
		}

		return false;
	}

	private static void ApplyTimingCommands(SequencingState state, IReadOnlyList<NoteCommand> commands)
	{
		foreach (NoteCommand command in commands)
		{
			switch (command)
			{
				case SetTempoCommand tempo:
					state.Tempo = tempo.TicksPerDiachron;
					break;

				case SetSpeedCommand speed:
					state.Speed = speed.TicksPerRow;
					break;
			}
		}
	}

	private static NoteEvent ResolveAt(NoteEvent noteEvent, double timeSeconds, SequencingContext context)
		=> noteEvent with
		{
			Offset = new MusicalTime(TimeSpan.FromSeconds(timeSeconds), 0.0),
			Target = context.MapTarget(noteEvent.Target),
		};

	private static int CompareTimingEvents(WorkingEvent left, WorkingEvent right)
	{
		int compare = left.TimingEligibilitySeconds!.Value.CompareTo(right.TimingEligibilitySeconds!.Value);
		if (compare != 0)
			return compare;

		return left.NoteEvent.EmissionOrder.CompareTo(right.NoteEvent.EmissionOrder);
	}

	private static int CompareResolvedEvents(NoteEvent left, NoteEvent right)
	{
		int compare = left.Offset.TimeOffset.CompareTo(right.Offset.TimeOffset);
		if (compare != 0)
			return compare;

		compare = CompareTargets(left.Target, right.Target);
		if (compare != 0)
			return compare;

		return left.EmissionOrder.CompareTo(right.EmissionOrder);
	}

	private static int CompareTargets(ChannelTarget left, ChannelTarget right)
	{
		if (left.Kind == ChannelTargetKind.Physical && right.Kind == ChannelTargetKind.Physical)
			return left.PhysicalChannel.CompareTo(right.PhysicalChannel);

		int compare = left.Kind.CompareTo(right.Kind);
		if (compare != 0)
			return compare;

		return left.VirtualChannelId.CompareTo(right.VirtualChannelId);
	}

	private static int FloorRow(double rowOffset)
	{
		if (rowOffset <= 0.0)
			return 0;

		if (rowOffset >= int.MaxValue)
			return int.MaxValue;

		return (int)Math.Floor(rowOffset);
	}

	private static double GetRowDurationSeconds(SequencingState state)
		=> SequencingConstants.Diachron.TotalSeconds / state.Tempo * state.Speed;
}
