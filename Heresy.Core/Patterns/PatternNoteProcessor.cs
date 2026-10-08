using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Diagnostics;
using Heresy.Core.Envelopes;
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

	private sealed class PatternLoopState
	{
		public int StartRow { get; set; }
		public byte RemainingRepeats { get; set; }
	}

	private sealed class RetriggerRequest
	{
		public required SequencingChannelState ChannelState { get; init; }
		public required byte Parameter { get; init; }
		public required bool HasNewNote { get; init; }
	}

	private sealed class TrackerTempoRequest
	{
		public required NoteEvent SourceEvent { get; init; }
		public required byte Parameter { get; init; }
		public required int PhysicalChannel { get; init; }
	}

	private sealed class RowTickTimeline
	{
		private readonly TrackerTimeMap _timeMap;

		public RowTickTimeline(double initialTempo)
			=> _timeMap = new TrackerTimeMap(initialTempo);

		public double EndTickPosition =>
			_timeMap.CurrentTick;

		public double EndSeconds =>
			_timeMap.CurrentTimeSeconds;

		public double CurrentTempo =>
			_timeMap.CurrentTempo;

		public void AppendConstantTicks(double trackerTicks)
			=> _timeMap.AppendConstantTicks(trackerTicks);

		public void AppendTempoRamp(
			double endingTempo,
			double trackerTicks)
			=> _timeMap.AppendTempoRamp(
				endingTempo,
				trackerTicks);

		public void SetTempo(double tempo)
			=> _timeMap.SetTempo(tempo);

		public double GetSecondsAtTickPosition(
			double tickPosition)
			=> _timeMap.GetTimeAtTick(tickPosition);

		public double GetTickAtSeconds(double seconds)
			=> _timeMap.GetTickAtTime(seconds);
	}

	private sealed class ResolvedCommands
	{
		public required IReadOnlyList<NoteCommand> Commands { get; init; }
		public required IReadOnlyList<NoteCommand> RowEndCommands { get; init; }
		public required IReadOnlyList<NoteCommand> RepeatCommands { get; init; }
		public RetriggerRequest? Retrigger { get; init; }
		public byte? NoteCutTick { get; init; }
		public byte? NoteDelayTick { get; init; }
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
		=> GenerateNotes(
			generator,
			context,
			output,
			startRow,
			out duration,
			out _);

	/// <summary>
	/// Generates one pattern invocation and reports any tracker sequence-control
	/// request which takes effect at the end of the final generated row.
	/// </summary>
	public static void GenerateNotes(
		IRawPatternNoteGenerator generator,
		SequencingContext context,
		INoteReceiver output,
		int startRow,
		out TimeSpan duration,
		out PatternFlowControl flowControl)
		=> GenerateNotes(
			generator,
			context,
			output,
			startRow,
			out duration,
			out flowControl,
			rowStarted: null);

	public static void GenerateNotes(
		IRawPatternNoteGenerator generator,
		SequencingContext context,
		INoteReceiver output,
		int startRow,
		out TimeSpan duration,
		out PatternFlowControl flowControl,
		Action<int, TimeSpan>? rowStarted)
	{
		ArgumentNullException.ThrowIfNull(generator);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);

		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		// The deferred Source-column vocabulary is emitted only by data
		// patterns. Script patterns keep their independent explicit note
		// semantics, including source ID 0.
		bool deferredDataSources = context.ResolvePatternSourcesAtRowTime
			&& generator is IDeferredSourcePatternGenerator;

		NoteScheduleBuilder rawBuilder = new();
		generator.GenerateRawNotes(context,
			new ChronologicalRawPatternNoteReceiver(rawBuilder, context.Diagnostics), out double rowCount);
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

		(events, effectiveRowCount, List<int> sourceRows) =
			ExpandPatternLoops(
				events,
				effectiveRowCount,
				context,
				startRow);

		(events, effectiveRowCount, flowControl) =
			ExtractPatternFlowControl(
				events,
				effectiveRowCount,
				sourceRows);

		int generatedRowCount =
			(int)Math.Ceiling(effectiveRowCount);
		if (sourceRows.Count > generatedRowCount)
		{
			sourceRows.RemoveRange(
				generatedRowCount,
				sourceRows.Count - generatedRowCount);
		}

		List<NoteEvent> resolved = new(events.Count);
		List<WorkingEvent> deferredTimingEvents = [];

		double rowStartSeconds = 0.0;
		int wholeRowCount = (int)Math.Ceiling(effectiveRowCount);

		for (int row = 0; row < wholeRowCount; row++)
		{
			if (rowStarted is not null)
			{
				rowStarted(
					sourceRows[row],
					TimeSpanFromSeconds(
						rowStartSeconds));
			}

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

			if (!context.IsPreparingFlattenedChild
				&& context.DeferredTempoEvents.HasPending
				&& dueTimingEvents.Count > 0)
			{
				throw new NotSupportedException(
					"Intervening parent tempo changes and deferred child timing require concurrent row scheduling.");
			}
			dueTimingEvents.Sort(CompareTimingEvents);
			List<TrackerTempoRequest> tempoRequests = [];

			foreach (WorkingEvent workingEvent in dueTimingEvents)
			{
				IReadOnlyList<NoteCommand> timingCommands =
					ResolveTimingCommands(
						workingEvent.NoteEvent,
						context,
						tempoRequests);

				if (timingCommands.Count != 0)
				{
					resolved.Add(
						ResolveAt(
							workingEvent.NoteEvent,
							timingCommands,
							rowStartSeconds,
							context,
							SyntheticOrder(
								workingEvent.NoteEvent.EmissionOrder,
								0)));
				}

				deferredTimingEvents.Remove(workingEvent);
			}

			int fineDelayTicks =
				GetFinePatternDelayTicksForRow(events, row);
			int rowSpanTickCount =
				checked(context.State.Speed + fineDelayTicks);
			byte patternDelayRows =
				GetPatternDelayRowsForRow(events, row);
			double rowEnd = Math.Min(
				row + 1.0,
				effectiveRowCount);
			double rowFraction = rowEnd - row;
			double rowEndTickPosition =
				context.State.Speed * rowFraction
					+ fineDelayTicks
					+ patternDelayRows * rowSpanTickCount;

			double initialRowTempo = context.State.Tempo;
			RowTickTimeline tickTimeline =
				BuildRowTickTimeline(
					rowStartSeconds,
					rowEndTickPosition,
					rowSpanTickCount,
					tempoRequests,
					context,
					resolved);

			double rowEndSeconds =
				rowStartSeconds
					+ tickTimeline.GetSecondsAtTickPosition(
						rowEndTickPosition);
			int rowTickSpan = checked(
				rowSpanTickCount
					* (patternDelayRows + 1));
			int rowSpeed = context.State.Speed;
			List<(double Tick, double Tempo)> childTempoChanges = [];
			List<(NoteEvent Event, double CommandTime)> delayedRowClears = [];
			if (!context.IsPreparingFlattenedChild)
			{
				ApplyDueDeferredTempoChanges(
					context, rowStartSeconds, rowStartSeconds,
					initialRowTempo, rowEndTickPosition,
					childTempoChanges, ref tickTimeline, ref rowEndSeconds);
			}
			// Process ordinary events by their actual position on the
			// row's initial time map, not by source emission order.
			// This also gives deterministic channel/emission tie-breaking
			// for simultaneous flattened invocations.
			List<WorkingEvent> ordinaryRowEvents = new(events);
			ordinaryRowEvents.Sort((left, right) =>
			{
				double a = tickTimeline.GetSecondsAtTickPosition(
					Math.Max(0.0, left.RowOffset - row) * rowSpeed)
					+ left.TimeOffsetSeconds;
				double b = tickTimeline.GetSecondsAtTickPosition(
					Math.Max(0.0, right.RowOffset - row) * rowSpeed)
					+ right.TimeOffsetSeconds;
				int compare = a.CompareTo(b);
				if (compare != 0)
					return compare;
				compare = CompareTargets(
					context.MapTarget(left.NoteEvent.Target),
					context.MapTarget(right.NoteEvent.Target));
				return compare != 0 ? compare
					: left.NoteEvent.EmissionOrder.CompareTo(
						right.NoteEvent.EmissionOrder);
			});
			int? rowTicksOverride =
				fineDelayTicks == 0
					? null
					: rowSpanTickCount;

			foreach (WorkingEvent workingEvent in ordinaryRowEvents)
			{
				IReadOnlyList<NoteCommand> ordinaryCommands =
					RemoveTimingCommands(
						workingEvent.NoteEvent.Commands);
				if (ordinaryCommands.Count == 0)
					continue;

				NoteEvent ordinaryEvent =
					workingEvent.NoteEvent with
					{
						Commands = ordinaryCommands,
					};

				bool isFinalEndpoint =
					row == wholeRowCount - 1
					&& workingEvent.RowOffset == effectiveRowCount;

				if (workingEvent.RowOffset < row)
					continue;
				if (!isFinalEndpoint
					&& workingEvent.RowOffset >= row + 1.0)
					continue;

				double fraction =
					workingEvent.RowOffset - row;
				if (fraction > rowFraction)
					continue;

				double eventTickPosition =
					fraction * rowSpeed;
				double eventTimeSeconds =
					rowStartSeconds
					+ tickTimeline.GetSecondsAtTickPosition(
						eventTickPosition)
					+ workingEvent.TimeOffsetSeconds;
				if (!context.IsPreparingFlattenedChild)
				{
					ApplyDueDeferredTempoChanges(
						context, rowStartSeconds, eventTimeSeconds,
						initialRowTempo, rowEndTickPosition,
						childTempoChanges, ref tickTimeline, ref rowEndSeconds);
					eventTimeSeconds =
						rowStartSeconds
						+ tickTimeline.GetSecondsAtTickPosition(eventTickPosition)
						+ workingEvent.TimeOffsetSeconds;
				}

				ResolvedCommands commands = ResolveCommands(
					ordinaryEvent,
					context,
					rowTicksOverride,
					deferredDataSources);

				double commandTickPosition =
					eventTickPosition;
				double commandTimeSeconds =
					eventTimeSeconds;
				bool executeCommands = true;

				if (commands.NoteDelayTick.HasValue)
				{
					int delayTick = EffectiveSCommandTick(
						commands.NoteDelayTick.Value);

					commandTickPosition =
						eventTickPosition + delayTick;

					executeCommands =
						delayTick < rowSpanTickCount
						&& commandTickPosition
							< tickTimeline.EndTickPosition + 1e-9;

					if (executeCommands)
					{
						commandTimeSeconds =
							rowStartSeconds
								+ tickTimeline
									.GetSecondsAtTickPosition(
										commandTickPosition)
								+ workingEvent.TimeOffsetSeconds;

						executeCommands =
							commandTimeSeconds < rowEndSeconds;
					}
				}

				if (executeCommands
					&& commands.Commands.Count != 0)
				{
					NoteEvent resolvedEvent = ResolveAt(
						ordinaryEvent,
						commands.Commands,
						commandTimeSeconds,
						context,
						SyntheticOrder(
							ordinaryEvent.EmissionOrder,
							1));
					// Flattened children must be generated here, while the
					// parent still has this row's tracker state. Waiting
					// until Freeze() loses effect memory and tempo ordering.
					double tempoBeforeChild = context.State.Tempo;
					int speedBeforeChild = context.State.Speed;
					IReadOnlyList<NoteEvent>? expanded =
						context.FlattenedSourceExpander?.Expand(
							resolvedEvent, context);
					if (expanded is null)
						resolved.Add(resolvedEvent);
					else
					{
						resolved.AddRange(expanded);
						// Flattened children are allowed to set the tempo at
						// the instant they are invoked, including halfway
						// through a parent row. Splice that change into the
						// remainder of the parent's tracker-tick map.
						// A future child tempo change needs concurrent child
						// scheduling and is intentionally not guessed here.
						bool hasDeferredTempo = false;
						foreach (NoteEvent nested in expanded)
						{
							foreach (NoteCommand command in nested.Commands)
							{
								if (command is SetTempoRampCommand)
									throw new NotSupportedException(
										"Flattened child tempo ramps need concurrent row scheduling.");
								if (command is SetSpeedCommand speed
									&& nested.Offset.TimeOffset
										> resolvedEvent.Offset.TimeOffset)
									throw new NotSupportedException(
										"Deferred flattened child speed changes need concurrent row scheduling.");
								if (command is not SetTempoCommand setTempo)
									continue;
								double delta = (nested.Offset.TimeOffset
									- resolvedEvent.Offset.TimeOffset).TotalSeconds;
								if (delta > 1e-7)
								{
									if (tempoRequests.Count != 0
										|| fineDelayTicks != 0 || patternDelayRows != 0)
										throw new NotSupportedException(
											"Concurrent parent timing effects and deferred flattened child tempo changes need concurrent row scheduling.");
									context.DeferredTempoEvents.Schedule(
										context.TimelineOrigin + nested.Offset.TimeOffset,
										setTempo.TicksPerDiachron);
									hasDeferredTempo = true;
									continue;
								}
								if (tempoRequests.Count != 0
									|| fineDelayTicks != 0 || patternDelayRows != 0)
									throw new NotSupportedException(
										"Concurrent parent tempo slides or row delays and flattened child tempo changes need a unified timeline.");
								childTempoChanges.Add((eventTickPosition,
									setTempo.TicksPerDiachron));
								tickTimeline = BuildTimelineWithChildTempoChanges(
									initialRowTempo,
									rowEndTickPosition,
									childTempoChanges);
								rowEndSeconds = rowStartSeconds
									+ tickTimeline.GetSecondsAtTickPosition(
										rowEndTickPosition);
							}
						}
						if (hasDeferredTempo)
						{
							// Preparing the child ran its future rows against the
							// shared state. Undo that premature tempo mutation.
							// Immediate sets at the invocation still take effect.
							context.State.Tempo = childTempoChanges.Count == 0
								? tempoBeforeChild
								: childTempoChanges[^1].Tempo;
							context.State.Speed = speedBeforeChild;
						}
					}
				}

				if (executeCommands
					&& commands.Retrigger is not null)
				{
					ExpandRetrigger(
						resolved,
						ordinaryEvent,
						commands.Retrigger,
						commandTickPosition,
						workingEvent.TimeOffsetSeconds,
						rowStartSeconds,
						rowEndSeconds,
						rowTickSpan,
						tickTimeline,
						context);
				}

				if (executeCommands
					&& patternDelayRows != 0)
				{
					if (commands.NoteDelayTick.HasValue
						&& commands.Commands.Count != 0)
					{
						int delayTick = EffectiveSCommandTick(
							commands.NoteDelayTick.Value);

						for (int repeat = 1;
							repeat <= patternDelayRows;
							repeat++)
						{
							double repeatTickPosition =
								repeat * rowSpanTickCount
									+ eventTickPosition
									+ delayTick;
							if (repeatTickPosition
								>= tickTimeline.EndTickPosition
									+ 1e-9)
							{
								break;
							}

							double repeatTimeSeconds =
								rowStartSeconds
									+ tickTimeline
										.GetSecondsAtTickPosition(
											repeatTickPosition)
									+ workingEvent.TimeOffsetSeconds;

							if (repeatTimeSeconds
								>= rowEndSeconds)
							{
								break;
							}

							resolved.Add(
								ResolveAt(
									ordinaryEvent,
									commands.Commands,
									repeatTimeSeconds,
									context,
									SyntheticOrder(
										ordinaryEvent.EmissionOrder,
										2)));
						}
					}
					else if (commands.RepeatCommands.Count != 0)
					{
						for (int repeat = 1;
							repeat <= patternDelayRows;
							repeat++)
						{
							double repeatTickPosition =
								repeat * rowSpanTickCount
									+ eventTickPosition;
							if (repeatTickPosition
								>= tickTimeline.EndTickPosition
									+ 1e-9)
							{
								break;
							}

							double repeatTimeSeconds =
								rowStartSeconds
									+ tickTimeline
										.GetSecondsAtTickPosition(
											repeatTickPosition)
									+ workingEvent.TimeOffsetSeconds;

							if (repeatTimeSeconds
								>= rowEndSeconds)
							{
								break;
							}

							resolved.Add(
								ResolveAt(
									ordinaryEvent,
									commands.RepeatCommands,
									repeatTimeSeconds,
									context,
									SyntheticOrder(
										ordinaryEvent.EmissionOrder,
										2)));
						}
					}
				}

				if (commands.NoteCutTick.HasValue)
				{
					int cutTick = EffectiveSCommandTick(
						commands.NoteCutTick.Value);
					double cutTickPosition =
						eventTickPosition + cutTick;

					if (cutTick < rowSpanTickCount
						&& cutTickPosition
							< tickTimeline.EndTickPosition + 1e-9)
					{
						double cutTimeSeconds =
							rowStartSeconds
								+ tickTimeline
									.GetSecondsAtTickPosition(
										cutTickPosition)
								+ workingEvent.TimeOffsetSeconds;

						if (cutTimeSeconds < rowEndSeconds)
						{
							resolved.Add(
								new NoteEvent(
									new MusicalTime(
										TimeSpanFromSeconds(
											cutTimeSeconds),
										0.0),
									context.MapTarget(
										ordinaryEvent.Target),
									new NoteCommand[]
										{
											new NoteCutCommand(),
										},
									SyntheticOrder(
										ordinaryEvent.EmissionOrder,
										4)));
						}
					}
				}

				if (executeCommands
					&& commands.RowEndCommands.Count != 0)
				{
					double clearTimeSeconds = Math.Max(
						rowEndSeconds,
						commandTimeSeconds);
					delayedRowClears.Add((
						new NoteEvent(
							new MusicalTime(TimeSpan.Zero, 0.0),
							context.MapTarget(ordinaryEvent.Target),
							commands.RowEndCommands,
							SyntheticOrder(ordinaryEvent.EmissionOrder, 5)),
						commandTimeSeconds));
				}
			}

			if (!context.IsPreparingFlattenedChild)
			{
				ApplyDueDeferredTempoChanges(
					context, rowStartSeconds, rowEndSeconds,
					initialRowTempo, rowEndTickPosition,
					childTempoChanges, ref tickTimeline, ref rowEndSeconds);
			}
			foreach ((NoteEvent clear, double commandTime) in delayedRowClears)
			{
				resolved.Add(clear with
				{
					Offset = new MusicalTime(
						TimeSpanFromSeconds(
								Math.Max(rowEndSeconds, commandTime)),
						0.0),
				});
			}
			rowStartSeconds = rowEndSeconds;
		}

		duration = TimeSpanFromSeconds(rowStartSeconds);

		// Child compilers own their emission-order counters, so two
		// flattened effects can have identical timestamps, targets and
		// emission orders. Preserve their resolution order as the final
		// deterministic tie-breaker (List.Sort itself is not stable).
		(NoteEvent Event, int Index)[] indexed =
			new (NoteEvent Event, int Index)[resolved.Count];
		for (int i = 0; i < resolved.Count; i++)
			indexed[i] = (resolved[i], i);
		Array.Sort(indexed, (left, right) =>
		{
			int comparison = CompareResolvedEvents(left.Event, right.Event);
			return comparison != 0
				? comparison
				: left.Index.CompareTo(right.Index);
		});
		foreach ((NoteEvent noteEvent, _) in indexed)
			output.Append(noteEvent);
	}

	public static void GenerateNotes(
		IRawPatternNoteGenerator generator,
		SequencingContext context,
		INoteReceiver output,
		out TimeSpan duration)
		=> GenerateNotes(generator, context, output, 0, out duration);

	private static (
		List<WorkingEvent> Events,
		double RowCount,
		PatternFlowControl FlowControl)
		ExtractPatternFlowControl(
			List<WorkingEvent> events,
			double rowCount,
			IReadOnlyList<int> sourceRows)
	{
		int? controlRow = null;

		foreach (WorkingEvent workingEvent in events)
		{
			if (workingEvent.RowOffset < 0.0
				|| workingEvent.RowOffset >= rowCount)
			{
				continue;
			}

			foreach (NoteCommand command in workingEvent.NoteEvent.Commands)
			{
				if (command is not ApplyTrackerOrderJumpCommand
					&& command is not ApplyTrackerPatternBreakCommand)
				{
					continue;
				}

				int row = FloorRow(workingEvent.RowOffset);
				if (!controlRow.HasValue || row < controlRow.Value)
					controlRow = row;
			}
		}

		if (!controlRow.HasValue)
		{
			return (
				events,
				rowCount,
				PatternFlowControl.None);
		}

		byte? orderJump = null;
		byte? breakRow = null;
		int finalRow = controlRow.Value;

		foreach (WorkingEvent workingEvent in events)
		{
			if (FloorRow(workingEvent.RowOffset) != finalRow)
				continue;

			foreach (NoteCommand command in workingEvent.NoteEvent.Commands)
			{
				switch (command)
				{
					case ApplyTrackerOrderJumpCommand jump:
						orderJump = jump.Order;
						break;

					case ApplyTrackerPatternBreakCommand patternBreak:
						breakRow = patternBreak.Row;
						break;
				}
			}
		}

		double truncatedRowCount = Math.Min(
			rowCount,
			finalRow + 1.0);
		List<WorkingEvent> filtered = [];

		foreach (WorkingEvent workingEvent in events)
		{
			if (workingEvent.RowOffset >= truncatedRowCount)
				continue;

			IReadOnlyList<NoteCommand> commands =
				RemoveSequenceControlCommands(
					workingEvent.NoteEvent.Commands);
			if (commands.Count == 0)
				continue;

			NoteEvent noteEvent = workingEvent.NoteEvent with
			{
				Commands = commands,
			};

			filtered.Add(new WorkingEvent
			{
				NoteEvent = noteEvent,
				RowOffset = workingEvent.RowOffset,
				TimeOffsetSeconds = workingEvent.TimeOffsetSeconds,
				AffectsTiming = AffectsTiming(noteEvent),
			});
		}

		return (
			filtered,
			truncatedRowCount,
			new PatternFlowControl(
				orderJump,
				breakRow)
			{
				SourceRow =
					finalRow < sourceRows.Count
						? sourceRows[finalRow]
						: null,
			});
	}

	private static IReadOnlyList<NoteCommand>
		RemoveSequenceControlCommands(
			IReadOnlyList<NoteCommand> commands)
	{
		List<NoteCommand>? filtered = null;

		for (int i = 0; i < commands.Count; i++)
		{
			if (commands[i] is not ApplyTrackerOrderJumpCommand
				&& commands[i] is not ApplyTrackerPatternBreakCommand)
			{
				filtered?.Add(commands[i]);
				continue;
			}

			filtered ??= CopyCommandsBefore(commands, i);
		}

		return filtered ?? commands;
	}

	private static (
		List<WorkingEvent> Events,
		double RowCount,
		List<int> SourceRows)
		ExpandPatternLoops(
			List<WorkingEvent> events,
			double rowCount,
			SequencingContext context,
			int sourceRowOffset)
	{
		if (!ContainsPatternLoop(events)
			|| rowCount <= 0.0)
		{
			List<int> linearRows = [];
			int wholeRows =
				(int)Math.Ceiling(rowCount);
			for (int row = 0; row < wholeRows; row++)
			{
				linearRows.Add(
					checked(
						sourceRowOffset + row));
			}
			return (
				events,
				rowCount,
				linearRows);
		}

		int sourceWholeRowCount = (int)Math.Ceiling(rowCount);
		Dictionary<int, PatternLoopState> loopStates = [];
		List<WorkingEvent> expanded = [];
		List<WorkingEvent> endpoints = [];
		List<int> sourceRows = [];

		foreach (WorkingEvent workingEvent in events)
		{
			if (workingEvent.RowOffset == rowCount)
				endpoints.Add(workingEvent);
		}

		int sourceRow = 0;
		double expandedRow = 0.0;
		int rowVisits = 0;

		while (sourceRow < sourceWholeRowCount)
		{
			if (++rowVisits > NoteScheduleBuilder.MaximumGeneratedNotes)
			{
				throw new SequencingResourceLimitException(
					$"Pattern-loop expansion exceeded {NoteScheduleBuilder.MaximumGeneratedNotes:N0} row visits.");
			}

			double sourceRowEnd = Math.Min(sourceRow + 1.0, rowCount);
			double rowSpan = sourceRowEnd - sourceRow;
			if (!(rowSpan > 0.0))
				break;

			sourceRows.Add(
				checked(
					sourceRowOffset + sourceRow));

			int expandedVisitStart = expanded.Count;

			foreach (WorkingEvent workingEvent in events)
			{
				if (workingEvent.RowOffset == rowCount)
					continue;
				if (workingEvent.RowOffset < sourceRow
					|| workingEvent.RowOffset >= sourceRowEnd)
				{
					continue;
				}

				IReadOnlyList<NoteCommand> commands =
					RemovePatternLoopCommands(
						workingEvent.NoteEvent.Commands);
				if (commands.Count == 0)
					continue;

				double fraction =
					workingEvent.RowOffset - sourceRow;
				NoteEvent expandedEvent =
					workingEvent.NoteEvent with
					{
						Offset = new MusicalTime(
							workingEvent.NoteEvent.Offset.TimeOffset,
							expandedRow + fraction),
						Commands = commands,
					};

				expanded.Add(new WorkingEvent
				{
					NoteEvent = expandedEvent,
					RowOffset = expandedRow + fraction,
					TimeOffsetSeconds =
						workingEvent.TimeOffsetSeconds,
					AffectsTiming = AffectsTiming(expandedEvent),
				});
			}

			int nextSourceRow = sourceRow + 1;
			int? firstActiveLoopChannel = null;
			List<WorkingEvent> loopEvents =
				GetPatternLoopEventsForRow(
					events,
					sourceRow,
					sourceRowEnd,
					context);
			foreach (WorkingEvent workingEvent in loopEvents)
			{
				int physicalChannel =
					context.MapPhysicalChannel(
						workingEvent.NoteEvent.Target.PhysicalChannel);

				if (!loopStates.TryGetValue(
					physicalChannel,
					out PatternLoopState? state))
				{
					state = new PatternLoopState();
					loopStates.Add(physicalChannel, state);
				}

				foreach (NoteCommand command
					in workingEvent.NoteEvent.Commands)
				{
					if (command is not ApplyTrackerPatternLoopCommand loop)
						continue;

					if (loop.RepeatCount == 0)
					{
						state.StartRow = sourceRow;
						continue;
					}

					if (state.RemainingRepeats == 0)
					{
						state.RemainingRepeats =
							loop.RepeatCount;
						nextSourceRow = state.StartRow;
						firstActiveLoopChannel ??=
							physicalChannel;
						continue;
					}

					state.RemainingRepeats--;
					if (state.RemainingRepeats != 0)
					{
						nextSourceRow = state.StartRow;
						firstActiveLoopChannel ??=
							physicalChannel;
					}
					else
					{
						state.StartRow = sourceRow + 1;
					}
				}
			}

			SuppressPatternBreakCommandsAfterActiveLoop(
				expanded,
				expandedVisitStart,
				firstActiveLoopChannel,
				context);

			expandedRow += rowSpan;

			if (rowSpan < 1.0
				&& nextSourceRow != sourceRow + 1)
			{
				throw new InvalidOperationException(
					"Tracker pattern loops cannot jump from a fractional final row.");
			}

			sourceRow = nextSourceRow;
		}

		foreach (WorkingEvent endpoint in endpoints)
		{
			IReadOnlyList<NoteCommand> commands =
				RemovePatternLoopCommands(
					endpoint.NoteEvent.Commands);
			if (commands.Count == 0)
				continue;

			NoteEvent expandedEvent =
				endpoint.NoteEvent with
				{
					Offset = new MusicalTime(
						endpoint.NoteEvent.Offset.TimeOffset,
						expandedRow),
					Commands = commands,
				};

			expanded.Add(new WorkingEvent
			{
				NoteEvent = expandedEvent,
				RowOffset = expandedRow,
				TimeOffsetSeconds = endpoint.TimeOffsetSeconds,
				AffectsTiming = AffectsTiming(expandedEvent),
			});
		}

		return (
			expanded,
			expandedRow,
			sourceRows);
	}

	private static bool ContainsPatternLoop(
		IReadOnlyList<WorkingEvent> events)
	{
		foreach (WorkingEvent workingEvent in events)
		{
			foreach (NoteCommand command
				in workingEvent.NoteEvent.Commands)
			{
				if (command is ApplyTrackerPatternLoopCommand)
					return true;
			}
		}

		return false;
	}

	private static void SuppressPatternBreakCommandsAfterActiveLoop(
		List<WorkingEvent> expanded,
		int startIndex,
		int? firstActiveLoopChannel,
		SequencingContext context)
	{
		if (!firstActiveLoopChannel.HasValue)
			return;

		for (int i = startIndex; i < expanded.Count; i++)
		{
			WorkingEvent workingEvent = expanded[i];
			ChannelTarget target = workingEvent.NoteEvent.Target;
			if (target.Kind != ChannelTargetKind.Physical
				|| context.MapPhysicalChannel(target.PhysicalChannel)
					< firstActiveLoopChannel.Value)
			{
				continue;
			}

			IReadOnlyList<NoteCommand> commands =
				RemovePatternBreakCommands(
					workingEvent.NoteEvent.Commands);
			if (commands.Count == workingEvent.NoteEvent.Commands.Count)
				continue;

			NoteEvent noteEvent = workingEvent.NoteEvent with
			{
				Commands = commands,
			};
			expanded[i] = new WorkingEvent
			{
				NoteEvent = noteEvent,
				RowOffset = workingEvent.RowOffset,
				TimeOffsetSeconds = workingEvent.TimeOffsetSeconds,
				AffectsTiming = AffectsTiming(noteEvent),
			};
		}
	}

	private static IReadOnlyList<NoteCommand>
		RemovePatternBreakCommands(
			IReadOnlyList<NoteCommand> commands)
	{
		List<NoteCommand>? filtered = null;

		for (int i = 0; i < commands.Count; i++)
		{
			if (commands[i] is not ApplyTrackerPatternBreakCommand)
			{
				filtered?.Add(commands[i]);
				continue;
			}

			filtered ??= CopyCommandsBefore(commands, i);
		}

		return filtered ?? commands;
	}

	private static IReadOnlyList<NoteCommand>
		RemovePatternLoopCommands(
			IReadOnlyList<NoteCommand> commands)
	{
		List<NoteCommand>? filtered = null;

		for (int i = 0; i < commands.Count; i++)
		{
			if (commands[i] is not ApplyTrackerPatternLoopCommand)
			{
				filtered?.Add(commands[i]);
				continue;
			}

			filtered ??= CopyCommandsBefore(commands, i);
		}

		return filtered ?? commands;
	}

	private static List<WorkingEvent> GetPatternLoopEventsForRow(
		IReadOnlyList<WorkingEvent> events,
		int sourceRow,
		double sourceRowEnd,
		SequencingContext context)
	{
		List<WorkingEvent> result = [];

		foreach (WorkingEvent workingEvent in events)
		{
			if (workingEvent.RowOffset == sourceRowEnd
				&& sourceRowEnd == Math.Ceiling(sourceRowEnd))
			{
				continue;
			}
			if (workingEvent.RowOffset < sourceRow
				|| workingEvent.RowOffset >= sourceRowEnd)
			{
				continue;
			}

			bool hasLoop = false;
			foreach (NoteCommand command
				in workingEvent.NoteEvent.Commands)
			{
				if (command is ApplyTrackerPatternLoopCommand)
				{
					hasLoop = true;
					break;
				}
			}

			if (!hasLoop)
				continue;

			if (workingEvent.NoteEvent.Target.Kind
				!= ChannelTargetKind.Physical)
			{
				throw new InvalidOperationException(
					"Tracker pattern loop requires a physical channel target.");
			}

			result.Add(workingEvent);
		}

		result.Sort((left, right) =>
		{
			int leftChannel =
				context.MapPhysicalChannel(
					left.NoteEvent.Target.PhysicalChannel);
			int rightChannel =
				context.MapPhysicalChannel(
					right.NoteEvent.Target.PhysicalChannel);
			int compare =
				leftChannel.CompareTo(rightChannel);
			if (compare != 0)
				return compare;

			return left.NoteEvent.EmissionOrder.CompareTo(
				right.NoteEvent.EmissionOrder);
		});

		return result;
	}

	private static bool AffectsTiming(NoteEvent noteEvent)
	{
		foreach (NoteCommand command in noteEvent.Commands)
		{
			if (command is SetTempoCommand
				or SetSpeedCommand
				or ApplyTrackerTempoCommand)
				return true;
		}

		return false;
	}

	private static IReadOnlyList<NoteCommand>
		ResolveTimingCommands(
			NoteEvent noteEvent,
			SequencingContext context,
			List<TrackerTempoRequest> tempoRequests)
	{
		List<NoteCommand> resolved = [];

		foreach (NoteCommand command in noteEvent.Commands)
		{
			switch (command)
			{
				case SetTempoCommand tempo:
					context.State.Tempo =
						tempo.TicksPerDiachron;
					resolved.Add(command);
					break;

				case SetSpeedCommand speed:
					context.State.Speed =
						speed.TicksPerRow;
					resolved.Add(command);
					break;

				case ApplyTrackerTempoCommand tempo:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker tempo");

					byte parameter =
						channelState.ResolveEffectParameter(
							EffectMemorySlot.Tempo,
							tempo.Parameter);

					if (parameter == 0)
						break;

					tempoRequests.Add(
						new TrackerTempoRequest
						{
							SourceEvent = noteEvent,
							Parameter = parameter,
							PhysicalChannel =
								context.MapPhysicalChannel(
									noteEvent.Target
										.PhysicalChannel),
						});

					if (parameter >= 0x20)
					{
						context.State.Tempo = parameter;
						resolved.Add(
							new SetTempoCommand(parameter));
					}
					break;
				}
			}
		}

		return resolved;
	}

	private static void ApplyDueDeferredTempoChanges(
		SequencingContext context,
		double rowStartSeconds,
		double throughSeconds,
		double initialRowTempo,
		double rowEndTickPosition,
		List<(double Tick, double Tempo)> changes,
		ref RowTickTimeline timeline,
		ref double rowEndSeconds)
	{
		while (context.DeferredTempoEvents.Peek() is
			DeferredTempoEventQueue.Change next)
		{
			double localTime = (next.At - context.TimelineOrigin).TotalSeconds;
			if (localTime > throughSeconds + 1e-8)
				break;
			double tick = localTime <= rowStartSeconds
				? 0.0
				: timeline.GetTickAtSeconds(localTime - rowStartSeconds);
			if (tick > rowEndTickPosition + 1e-8)
				break;
			context.DeferredTempoEvents.Pop();
			if (changes.Count > 0 && tick < changes[^1].Tick - 1e-8)
				throw new NotSupportedException(
					"Overlapping flattened tempo schedules require concurrent row scheduling.");
			changes.Add((Math.Clamp(tick, 0.0, rowEndTickPosition), next.Tempo));
			timeline = BuildTimelineWithChildTempoChanges(
				initialRowTempo, rowEndTickPosition, changes);
			rowEndSeconds = rowStartSeconds
				+ timeline.GetSecondsAtTickPosition(rowEndTickPosition);
			context.State.Tempo = next.Tempo;
		}
	}

	private static RowTickTimeline BuildTimelineWithChildTempoChanges(
		double initialTempo,
		double rowEndTick,
		IReadOnlyList<(double Tick, double Tempo)> changes)
	{
		RowTickTimeline timeline = new(initialTempo);
		double lastTick = 0.0;
		foreach ((double tick, double tempo) in changes)
		{
			if (tick < lastTick - 1e-9 || tick > rowEndTick + 1e-9)
				throw new InvalidOperationException(
					"Flattened tempo changes must be processed in nondecreasing tracker time.");
			if (tick > lastTick)
				timeline.AppendConstantTicks(tick - lastTick);
			timeline.SetTempo(tempo);
			lastTick = tick;
		}
		if (lastTick < rowEndTick)
			timeline.AppendConstantTicks(rowEndTick - lastTick);
		return timeline;
	}

	private static RowTickTimeline BuildRowTickTimeline(
		double rowStartSeconds,
		double rowEndTickPosition,
		int rowSpanTickCount,
		List<TrackerTempoRequest> tempoRequests,
		SequencingContext context,
		List<NoteEvent> resolved)
	{
		if (rowEndTickPosition < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(rowEndTickPosition));
		}
		if (rowSpanTickCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(rowSpanTickCount));
		}

		tempoRequests.Sort(
			(left, right) =>
			{
				int compare =
					left.PhysicalChannel.CompareTo(
						right.PhysicalChannel);
				if (compare != 0)
					return compare;

				return left.SourceEvent.EmissionOrder.CompareTo(
					right.SourceEvent.EmissionOrder);
			});

		RowTickTimeline timeline =
			new(context.State.Tempo);

		double remainingTicks = rowEndTickPosition;
		int spanIndex = 0;

		while (remainingTicks > 1e-12)
		{
			double spanTicks = Math.Min(
				rowSpanTickCount,
				remainingTicks);

			// Pattern-delay repetitions represent fresh compatibility rows.
			// Re-apply immediate tracker tempo sets at the repeated-row start.
			if (spanIndex != 0)
			{
				foreach (TrackerTempoRequest request
					in tempoRequests)
				{
					if (request.Parameter < 0x20)
						continue;

					double oldTempo = timeline.CurrentTempo;
					double newTempo =
						ResolveTrackerTempoAtTick(
							oldTempo,
							request.Parameter,
							firstTick: true);

					if (newTempo == oldTempo)
						continue;

					timeline.SetTempo(newTempo);
					resolved.Add(
						ResolveAt(
							request.SourceEvent,
							new NoteCommand[]
								{
									new SetTempoCommand(
										newTempo),
								},
							rowStartSeconds
								+ timeline.EndSeconds,
							context,
							SyntheticOrder(
								request.SourceEvent
									.EmissionOrder,
								0)));
				}
			}

			double startingTempo = timeline.CurrentTempo;
			double endingTempo = startingTempo;

			int legacySlideTransitions = Math.Max(
				0,
				(int)Math.Ceiling(spanTicks - 1e-12)
					- 1);

			for (int transition = 0;
				transition < legacySlideTransitions;
				transition++)
			{
				foreach (TrackerTempoRequest request
					in tempoRequests)
				{
					if (request.Parameter >= 0x20)
						continue;

					endingTempo =
						ResolveTrackerTempoAtTick(
							endingTempo,
							request.Parameter,
							firstTick: false);
				}
			}

			if (Math.Abs(endingTempo - startingTempo) > 1e-12)
			{
				resolved.Add(
					new NoteEvent(
						new MusicalTime(
							TimeSpanFromSeconds(
								rowStartSeconds
									+ timeline.EndSeconds),
							0.0),
						ChannelTarget.Global,
						new NoteCommand[]
							{
								new SetTempoRampCommand(
									endingTempo,
									spanTicks),
							},
						SyntheticOrder(
							tempoRequests.Count == 0
								? 0
								: tempoRequests[0]
									.SourceEvent
									.EmissionOrder,
							0)));

				timeline.AppendTempoRamp(
					endingTempo,
					spanTicks);
			}
			else
			{
				timeline.AppendConstantTicks(
					spanTicks);
			}

			context.State.Tempo =
				timeline.CurrentTempo;
			remainingTicks -= spanTicks;
			spanIndex++;
		}

		return timeline;
	}

	internal static double ResolveTrackerTempoAtTick(
		double currentTempo,
		byte parameter,
		bool firstTick)
	{
		if (firstTick)
		{
			return parameter >= 0x20
				? parameter
				: currentTempo;
		}

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		return high switch
		{
			0 => Math.Max(32.0, currentTempo - low),
			1 => Math.Min(255.0, currentTempo + low),
			_ => currentTempo,
		};
	}

	private static IReadOnlyList<NoteCommand>
		RemoveTimingCommands(
			IReadOnlyList<NoteCommand> commands)
	{
		List<NoteCommand>? filtered = null;

		for (int i = 0; i < commands.Count; i++)
		{
			if (!IsTimingCommand(commands[i]))
			{
				filtered?.Add(commands[i]);
				continue;
			}

			filtered ??= CopyCommandsBefore(
				commands,
				i);
		}

		return filtered ?? commands;
	}

	private static bool IsTimingCommand(
		NoteCommand command)
		=> command is SetTempoCommand
			or SetSpeedCommand
			or ApplyTrackerTempoCommand;


	private static NoteEvent ResolveRowSourceCommands(
		NoteEvent noteEvent,
		SequencingContext context)
	{
		if (!context.ResolvePatternSourcesAtRowTime
			|| noteEvent.Target.Kind != ChannelTargetKind.Physical)
			return noteEvent;

		SequencingChannelState channelState =
			context.GetPhysicalChannelState(noteEvent.Target.PhysicalChannel);
		List<NoteCommand> commands = [];
		bool changed = false;
		foreach (NoteCommand command in noteEvent.Commands)
		{
			switch (command)
			{
				case SelectPatternSourceCommand selection:
					channelState.CurrentSourceId = selection.SourceId;
					changed = true;
					break;

				case StartNoteCommand start when start.SourceId.IsNone:
					changed = true;
					if (!channelState.CurrentSourceId.IsNone)
						commands.Add(start with
						{
							SourceId = channelState.CurrentSourceId,
						});
					else if (start.Volume.HasValue)
						commands.Add(new SetNoteVolumeCommand(start.Volume.Value));
					break;

				case ApplyTonePortamentoCommand tone
					when tone.TargetNote is { SourceId.IsNone: true }:
					changed = true;
					commands.Add(tone with
					{
						TargetNote = ResolvePortamentoSource(
							tone.TargetNote, channelState),
					});
					break;

				case ApplyTonePortamentoVolumeSlideCommand combined
					when combined.TargetNote is { SourceId.IsNone: true }:
					changed = true;
					commands.Add(combined with
					{
						TargetNote = ResolvePortamentoSource(
							combined.TargetNote, channelState),
					});
					break;

				case ApplyTrackerVolumeColumnCommand volume
					when volume.TargetNote is { SourceId.IsNone: true }:
					changed = true;
					commands.Add(volume with
					{
						TargetNote = ResolvePortamentoSource(
							volume.TargetNote, channelState),
					});
					break;

				default:
					commands.Add(command);
					break;
			}
		}
		return changed ? noteEvent with { Commands = commands } : noteEvent;
	}

	private static StartNoteCommand? ResolvePortamentoSource(
		StartNoteCommand target,
		SequencingChannelState channelState)
		=> channelState.CurrentSourceId.IsNone
			? null
			: target with { SourceId = channelState.CurrentSourceId };

	private static ResolvedCommands ResolveCommands(
		NoteEvent noteEvent,
		SequencingContext context,
		int? rowTicksOverride,
		bool deferredDataSources)
	{
		// Source selection is a row-level sequencer operation, not a raw
		// grid preprocessing side effect. In particular, a flattened child
		// may have replaced this physical channel's remembered source.
		if (deferredDataSources)
			noteEvent = ResolveRowSourceCommands(noteEvent, context);
		List<NoteCommand>? transformed =
			rowTicksOverride.HasValue
				? new List<NoteCommand>(noteEvent.Commands.Count)
				: null;
		List<NoteCommand> rowEndCommands = [];
		List<NoteCommand> repeatCommands = [];
		RetriggerRequest? retrigger = null;
		byte? noteCutTick = null;
		byte? noteDelayTick = null;

		for (int i = 0; i < noteEvent.Commands.Count; i++)
		{
			NoteCommand command = noteEvent.Commands[i];

			switch (command)
			{
				case ApplyTrackerVibratoWaveformCommand waveform:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker vibrato waveform");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					if (waveform.Value <= 3)
					{
						channelState.VibratoWaveform =
							(TrackerWaveform)waveform.Value;
					}
					break;
				}

				case ApplyVibratoCommand vibrato:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker vibrato");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameterNibbles(
						EffectMemorySlot.Vibrato,
						vibrato.Parameter);

					if ((vibrato.Parameter & 0x0F) != 0)
						channelState.VibratoDepthScale = 1.0;

					transformed.Add(new SetVibratoCommand(
						(byte)(parameter >> 4),
						(byte)(parameter & 0x0F),
						channelState.VibratoWaveform,
						channelState.VibratoDepthScale));
					rowEndCommands.Add(new ClearPitchModulationCommand());
					break;
				}

				case ApplyFineVibratoCommand vibrato:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker fine vibrato");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					byte parameter =
						channelState.ResolveEffectParameterNibbles(
							EffectMemorySlot.Vibrato,
							vibrato.Parameter);

					if ((vibrato.Parameter & 0x0F) != 0)
						channelState.VibratoDepthScale = 0.25;

					transformed.Add(
						new SetVibratoCommand(
							(byte)(parameter >> 4),
							(byte)(parameter & 0x0F),
							channelState.VibratoWaveform,
							channelState.VibratoDepthScale));
					rowEndCommands.Add(
						new ClearPitchModulationCommand());
					break;
				}

				case ApplyVibratoVolumeSlideCommand combined:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker vibrato plus volume slide");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					byte volumeParameter =
						channelState.ResolveEffectParameter(
							EffectMemorySlot.VolumeSlide,
							combined.Parameter);

					NoteCommand? volumeCommand =
						ApplyRowTickOverride(
							ResolveTrackerVolumeSlide(
								volumeParameter),
							rowTicksOverride);

					if (volumeCommand is not null)
					{
						transformed.Add(volumeCommand);
						AddRepeatCommand(
							repeatCommands,
							volumeCommand);

						if (volumeCommand is SetNoteVolumeSlideCommand)
						{
							rowEndCommands.Add(
								new ClearNoteVolumeSlideCommand());
						}
					}

					byte vibratoParameter =
						channelState.ResolveEffectParameterNibbles(
							EffectMemorySlot.Vibrato,
							0);

					transformed.Add(
						new SetVibratoCommand(
							(byte)(vibratoParameter >> 4),
							(byte)(vibratoParameter & 0x0F),
							channelState.VibratoWaveform,
							channelState.VibratoDepthScale));
					rowEndCommands.Add(
						new ClearPitchModulationCommand());
					break;
				}

				case ApplyTrackerVolumeColumnCommand volumeColumn:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker volume-column effect");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					switch (volumeColumn.Kind)
					{
						case TrackerVolumeColumnEffectKind.FineVolumeUp:
						case TrackerVolumeColumnEffectKind.FineVolumeDown:
						case TrackerVolumeColumnEffectKind.VolumeSlideUp:
						case TrackerVolumeColumnEffectKind.VolumeSlideDown:
						{
							byte parameter =
								channelState.ResolveEffectParameter(
									EffectMemorySlot.VolumeColumnSlide,
									volumeColumn.Parameter);
							if (parameter == 0)
								break;

							double direction =
								volumeColumn.Kind is TrackerVolumeColumnEffectKind.FineVolumeUp
									or TrackerVolumeColumnEffectKind.VolumeSlideUp
									? 1.0
									: -1.0;
							bool fine =
								volumeColumn.Kind is TrackerVolumeColumnEffectKind.FineVolumeUp
									or TrackerVolumeColumnEffectKind.FineVolumeDown;

							NoteCommand resolved = fine
								? new AdjustCurrentNoteVolumeCommand(
									direction * parameter)
								: ApplyRowTickOverride(
									new SetNoteVolumeSlideCommand(
										direction * parameter),
									rowTicksOverride)!;

							transformed.Add(resolved);
							AddRepeatCommand(repeatCommands, resolved);
							if (!fine)
							{
								rowEndCommands.Add(
									new ClearNoteVolumeSlideCommand());
							}
							break;
						}

						case TrackerVolumeColumnEffectKind.PitchSlideDown:
						case TrackerVolumeColumnEffectKind.PitchSlideUp:
						{
							byte supplied = volumeColumn.Parameter == 0
								? (byte)0
								: checked((byte)(volumeColumn.Parameter * 4));
							byte parameter =
								channelState.ResolveEffectParameter(
									EffectMemorySlot.PitchSlide,
									supplied);
							if (parameter == 0)
								break;

							double direction =
								volumeColumn.Kind == TrackerVolumeColumnEffectKind.PitchSlideUp
									? 1.0
									: -1.0;
							SetPitchSlideCommand resolved =
								(SetPitchSlideCommand)ApplyRowTickOverride(
									new SetPitchSlideCommand(
										direction * parameter * 4.0),
									rowTicksOverride)!;
							transformed.Add(resolved);
							AddRepeatCommand(repeatCommands, resolved);
							rowEndCommands.Add(new ClearPitchSlideCommand());
							break;
						}

						case TrackerVolumeColumnEffectKind.TonePortamento:
						{
							byte supplied = ResolveVolumeColumnTonePortamentoSpeed(
								volumeColumn.Parameter);
							byte parameter =
								channelState.ResolveEffectParameter(
									EffectMemorySlot.TonePortamento,
									supplied);

							if (parameter != 0 || volumeColumn.TargetNote is not null)
							{
								SetTonePortamentoCommand resolved =
									(SetTonePortamentoCommand)ApplyRowTickOverride(
										new SetTonePortamentoCommand(
											parameter * 4.0,
											volumeColumn.TargetNote,
											Glissando: channelState.GlissandoEnabled),
										rowTicksOverride)!;
								transformed.Add(resolved);
								repeatCommands.Add(
									resolved with { TargetNote = null });
								rowEndCommands.Add(
									new ClearTonePortamentoCommand());
							}
							break;
						}

						case TrackerVolumeColumnEffectKind.Vibrato:
						{
							byte parameter =
								channelState.ResolveEffectParameterNibbles(
									EffectMemorySlot.Vibrato,
									volumeColumn.Parameter);
							if (volumeColumn.Parameter != 0)
								channelState.VibratoDepthScale = 1.0;

							transformed.Add(
								new SetVibratoCommand(
									(byte)(parameter >> 4),
									(byte)(parameter & 0x0F),
									channelState.VibratoWaveform,
									channelState.VibratoDepthScale));
							rowEndCommands.Add(
								new ClearPitchModulationCommand());
							break;
						}

						default:
							throw new InvalidOperationException(
								$"Unsupported volume-column effect {volumeColumn.Kind}.");
					}
					break;
				}

				case ApplyVolumeSlideCommand slide:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker volume slide");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.VolumeSlide,
						slide.Parameter);

					NoteCommand? resolved =
						ApplyRowTickOverride(
							ResolveTrackerVolumeSlide(parameter),
							rowTicksOverride);
					if (resolved is not null)
					{
						transformed.Add(resolved);
						AddRepeatCommand(repeatCommands, resolved);
						if (resolved is SetNoteVolumeSlideCommand)
							rowEndCommands.Add(new ClearNoteVolumeSlideCommand());
					}
					break;
				}

				case ApplyTrackerGlobalVolumeCommand volume:
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					if (volume.Parameter <= 128)
					{
						transformed.Add(
							new SetGlobalVolumeCommand(
								volume.Parameter / 128.0));
					}
					break;

				case ApplyGlobalVolumeSlideCommand slide:
					{
						SequencingChannelState channelState =
							GetTrackerChannelState(
								noteEvent,
								context,
								"Tracker global-volume slide");
						transformed ??= CopyCommandsBefore(
							noteEvent.Commands,
							i);

						byte parameter =
							channelState.ResolveEffectParameter(
								EffectMemorySlot.GlobalVolumeSlide,
								slide.Parameter);

						NoteCommand? resolved =
							ApplyRowTickOverride(
								ResolveTrackerGlobalVolumeSlide(
									parameter),
								rowTicksOverride);

						if (resolved is not null)
						{
							transformed.Add(resolved);
							AddRepeatCommand(
								repeatCommands,
								resolved);

							if (resolved
								is SetGlobalVolumeSlideCommand)
							{
								rowEndCommands.Add(
									new ClearGlobalVolumeSlideCommand());
							}
						}
						break;
					}

				case ApplyTrackerChannelVolumeCommand volume:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker channel volume");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					if (volume.Parameter <= 64)
					{
						transformed.Add(
							new SetOverallChannelVolumeCommand(
								volume.Parameter / 64.0));
					}
					break;

				case ApplyChannelVolumeSlideCommand slide:
					{
						SequencingChannelState channelState =
							GetTrackerChannelState(
								noteEvent,
								context,
								"Tracker channel-volume slide");
						transformed ??= CopyCommandsBefore(
							noteEvent.Commands,
							i);

						byte parameter =
							channelState.ResolveEffectParameter(
								EffectMemorySlot.ChannelVolumeSlide,
								slide.Parameter);

						NoteCommand? resolved =
							ApplyRowTickOverride(
								ResolveTrackerChannelVolumeSlide(
									parameter),
								rowTicksOverride);

						if (resolved is not null)
						{
							transformed.Add(resolved);
							AddRepeatCommand(
								repeatCommands,
								resolved);

							if (resolved
								is SetOverallChannelVolumeSlideCommand)
							{
								rowEndCommands.Add(
									new ClearOverallChannelVolumeSlideCommand());
							}
						}
						break;
					}

				case ApplyPitchSlideDownCommand slide:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker pitch slide");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.PitchSlide,
						slide.Parameter);

					NoteCommand? resolved =
						ApplyRowTickOverride(
							ResolveTrackerPitchSlide(
								parameter,
								direction: -1.0),
							rowTicksOverride);
					if (resolved is not null)
					{
						transformed.Add(resolved);
						AddRepeatCommand(repeatCommands, resolved);
						if (resolved is SetPitchSlideCommand)
							rowEndCommands.Add(new ClearPitchSlideCommand());
					}
					break;
				}

				case ApplyPitchSlideUpCommand slide:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker pitch slide");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.PitchSlide,
						slide.Parameter);

					NoteCommand? resolved =
						ApplyRowTickOverride(
							ResolveTrackerPitchSlide(
								parameter,
								direction: 1.0),
							rowTicksOverride);
					if (resolved is not null)
					{
						transformed.Add(resolved);
						AddRepeatCommand(repeatCommands, resolved);
						if (resolved is SetPitchSlideCommand)
							rowEndCommands.Add(new ClearPitchSlideCommand());
					}
					break;
				}

				case ApplyTrackerGlissandoControlCommand glissando:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker glissando control");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					channelState.GlissandoEnabled =
						glissando.Value != 0;
					break;
				}

				case ApplyTonePortamentoCommand tonePortamento:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker tone portamento");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.TonePortamento,
						tonePortamento.Parameter);

					if (parameter != 0 || tonePortamento.TargetNote is not null)
					{
						SetTonePortamentoCommand resolved =
							(SetTonePortamentoCommand)ApplyRowTickOverride(
								new SetTonePortamentoCommand(
									parameter * 4.0,
									tonePortamento.TargetNote,
									Glissando: channelState.GlissandoEnabled),
								rowTicksOverride)!;
						transformed.Add(resolved);
						repeatCommands.Add(
							resolved with { TargetNote = null });
						rowEndCommands.Add(new ClearTonePortamentoCommand());
					}
					break;
				}

				case ApplyTonePortamentoVolumeSlideCommand combined:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker tone portamento plus volume slide");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					byte toneParameter =
						channelState.ResolveEffectParameter(
							EffectMemorySlot.TonePortamento,
							0);

					if (toneParameter != 0
						|| combined.TargetNote is not null)
					{
						SetTonePortamentoCommand tone =
							(SetTonePortamentoCommand)ApplyRowTickOverride(
								new SetTonePortamentoCommand(
									toneParameter * 4.0,
									combined.TargetNote,
									Glissando:
										channelState.GlissandoEnabled),
								rowTicksOverride)!;

						transformed.Add(tone);
						repeatCommands.Add(
							tone with { TargetNote = null });
						rowEndCommands.Add(
							new ClearTonePortamentoCommand());
					}

					byte volumeParameter =
						channelState.ResolveEffectParameter(
							EffectMemorySlot.VolumeSlide,
							combined.Parameter);

					NoteCommand? volume =
						ApplyRowTickOverride(
							ResolveTrackerVolumeSlide(
								volumeParameter),
							rowTicksOverride);

					if (volume is not null)
					{
						transformed.Add(volume);
						AddRepeatCommand(
							repeatCommands,
							volume);

						if (volume is SetNoteVolumeSlideCommand)
						{
							rowEndCommands.Add(
								new ClearNoteVolumeSlideCommand());
						}
					}
					break;
				}

				case ApplyArpeggioCommand arpeggio:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker arpeggio");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.Arpeggio,
						arpeggio.Parameter);

					if (parameter != 0)
					{
						transformed.Add(new SetArpeggioCommand(
							(byte)(parameter >> 4),
							(byte)(parameter & 0x0F)));
						rowEndCommands.Add(new ClearArpeggioCommand());
					}
					break;
				}

				case ApplyTrackerTremoloWaveformCommand waveform:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker tremolo waveform");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					if (waveform.Value <= 3)
					{
						channelState.TremoloWaveform =
							(TrackerWaveform)waveform.Value;
					}
					break;
				}

				case ApplyTrackerMidiMacroSelectCommand macro:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker MIDI macro select");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);
					channelState.MidiMacroIndex = macro.Macro;
					break;
				}

				case ApplyTrackerMidiMacroCommand macro:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker MIDI macro");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					NoteCommand? resolvedMacro =
						context.TrackerMidiMacros.ResolvePcmCommand(
							channelState.MidiMacroIndex,
							macro.Parameter);
					if (resolvedMacro is not null)
						transformed.Add(resolvedMacro);
					break;
				}

				case ApplyTrackerPanbrelloWaveformCommand waveform:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker panbrello waveform");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					if (waveform.Value > 3)
						break;

					TrackerWaveform selected =
						(TrackerWaveform)waveform.Value;

					channelState.PanbrelloWaveform = selected;
					transformed.Add(
						new SetPanbrelloWaveformCommand(
							selected));
					break;
				}

				case ApplyTremoloCommand tremolo:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker tremolo");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameterNibbles(
						EffectMemorySlot.Tremolo,
						tremolo.Parameter);

					transformed.Add(new SetTremoloCommand(
						(byte)(parameter >> 4),
						(byte)(parameter & 0x0F),
						channelState.TremoloWaveform));
					rowEndCommands.Add(new ClearTremoloCommand());
					break;
				}

				case ApplyTremorCommand tremor:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker tremor");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					byte parameter =
						channelState.ResolveEffectParameter(
							EffectMemorySlot.Tremor,
							tremor.Parameter);

					byte onTicks = (byte)Math.Max(
						1,
						parameter >> 4);
					byte offTicks = (byte)Math.Max(
						1,
						parameter & 0x0F);

					SetTremorCommand resolved =
						(SetTremorCommand)ApplyRowTickOverride(
							new SetTremorCommand(
								onTicks,
								offTicks),
							rowTicksOverride)!;

					transformed.Add(resolved);
					AddRepeatCommand(
						repeatCommands,
						resolved);
					rowEndCommands.Add(
						new ClearTremorCommand());
					break;
				}

				case ApplyPanbrelloCommand panbrello:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker panbrello");
					transformed ??= CopyCommandsBefore(
						noteEvent.Commands,
						i);

					byte parameter =
						channelState.ResolveEffectParameterNibbles(
							EffectMemorySlot.Panbrello,
							panbrello.Parameter);

					SetPanbrelloCommand resolved =
						(SetPanbrelloCommand)ApplyRowTickOverride(
							new SetPanbrelloCommand(
								(byte)(parameter >> 4),
								(byte)(parameter & 0x0F),
								channelState.PanbrelloWaveform),
							rowTicksOverride)!;

					transformed.Add(resolved);
					AddRepeatCommand(
						repeatCommands,
						resolved);
					rowEndCommands.Add(
						new ClearPanbrelloCommand());
					break;
				}

				case ApplyRetriggerCommand rawRetrigger:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker retrigger");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.Retrigger,
						rawRetrigger.Parameter);

					bool hasNewNote = HasStartNote(noteEvent.Commands);

					retrigger = new RetriggerRequest
					{
						ChannelState = channelState,
						Parameter = parameter,
						HasNewNote = hasNewNote,
					};
					break;
				}

				case ApplySampleOffsetCommand rawOffset:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker sample offset");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameter(
						EffectMemorySlot.SampleOffset,
						rawOffset.Parameter);

					if (HasStartNote(noteEvent.Commands))
					{
						long sourceFrameOffset =
							((long)channelState.SampleOffsetHigh << 16)
							| ((long)parameter << 8);

						transformed.Add(new SetSourceFrameOffsetCommand(
							sourceFrameOffset));
					}
					break;
				}

				case ApplySampleOffsetHighCommand rawHighOffset:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(
							noteEvent,
							context,
							"Tracker high sample offset");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					channelState.SampleOffsetHigh =
						rawHighOffset.HighOffset;
					break;
				}

				case ApplyTrackerNoteCutCommand cut:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker note cut");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					noteCutTick = cut.Tick;
					break;

				case ApplyTrackerNoteDelayCommand delay:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker note delay");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					noteDelayTick = delay.Tick;
					break;

				case ApplyTrackerPatternDelayCommand:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker pattern delay");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					break;

				case ApplyTrackerFinePatternDelayCommand:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker fine pattern delay");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					break;

				case ApplyTrackerPastNoteActionCommand pastNote:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker past-note action");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					transformed.Add(
						new ApplyPastNoteActionCommand(
							pastNote.Action));
					break;

				case ApplyTrackerNewNoteActionCommand newNoteAction:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker new-note action");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);
					transformed.Add(
						new SetCurrentVoiceDisplacementActionCommand(
							newNoteAction.Action));
					break;

				case ApplyTrackerEnvelopeControlCommand envelope:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker envelope control");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					switch (envelope.Target)
					{
						case TrackerEnvelopeControlTarget.Volume:
							transformed.Add(
								new SetEnvelopeEnabledCommand(
									EnvelopeTarget.Volume,
									envelope.Enabled));
							break;

						case TrackerEnvelopeControlTarget.Panning:
							transformed.Add(
								new SetEnvelopeEnabledCommand(
									EnvelopeTarget.Panning,
									envelope.Enabled));
							break;

						case TrackerEnvelopeControlTarget.PitchOrFilter:
							transformed.Add(
								new SetEnvelopeEnabledCommand(
									EnvelopeTarget.Pitch,
									envelope.Enabled));
							transformed.Add(
								new SetEnvelopeEnabledCommand(
									EnvelopeTarget.Filter,
									envelope.Enabled));
							break;

						default:
							throw new InvalidOperationException(
								$"Unsupported tracker envelope target {envelope.Target}.");
					}
					break;

				case ApplyTrackerVolumeColumnPanningCommand panning:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker volume-column panning");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					if (!HasEffectColumnAbsolutePanning(noteEvent.Commands))
					{
						transformed.Add(
							new SetSpatialPositionCommand(
								new Vector3(
									TrackerPan64ToSpatialX(
										panning.Value),
									0.0f,
									0.0f)));
					}
					break;

				case ApplyTrackerPanningCommand panning:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker panning");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					int repeatedByte =
						panning.Value | (panning.Value << 4);
					int s8Pan64 = (repeatedByte + 2) >> 2;

					transformed.Add(
						new SetSpatialPositionCommand(
							new Vector3(
								TrackerPan64ToSpatialX(s8Pan64),
								0.0f,
								0.0f)));
					break;

				case ApplyTrackerPanning8BitCommand panning:
					GetTrackerChannelState(
						noteEvent,
						context,
						"Tracker 8-bit panning");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					int xPan64 = (panning.Parameter + 2) >> 2;
					transformed.Add(
						new SetSpatialPositionCommand(
							new Vector3(
								TrackerPan64ToSpatialX(xPan64),
								0.0f,
								0.0f)));
					break;

				case ApplyPanningSlideCommand slide:
					{
						SequencingChannelState channelState =
							GetTrackerChannelState(
								noteEvent,
								context,
								"Tracker panning slide");
						transformed ??= CopyCommandsBefore(
							noteEvent.Commands,
							i);

						byte parameter =
							channelState.ResolveEffectParameter(
								EffectMemorySlot.PanningSlide,
								slide.Parameter);

						NoteCommand? resolved =
							ApplyRowTickOverride(
								ResolveTrackerPanningSlide(parameter),
								rowTicksOverride);

						if (resolved is not null)
						{
							transformed.Add(resolved);
							AddRepeatCommand(
								repeatCommands,
								resolved);

							if (resolved is SetSpatialXSlideCommand)
							{
								rowEndCommands.Add(
									new ClearSpatialXSlideCommand());
							}
						}
						break;
					}

				default:
					command =
						ApplyRowTickOverride(
							command,
							rowTicksOverride)
						?? command;
					if (command is SetPitchSlideCommand)
						rowEndCommands.Add(new ClearPitchSlideCommand());

					if (command is SetNoteVolumeSlideCommand)
						rowEndCommands.Add(new ClearNoteVolumeSlideCommand());

					if (command is SetSpatialXSlideCommand)
						rowEndCommands.Add(new ClearSpatialXSlideCommand());

					if (command is SetOverallChannelVolumeSlideCommand)
					{
						rowEndCommands.Add(
							new ClearOverallChannelVolumeSlideCommand());
					}

					if (command is SetGlobalVolumeSlideCommand)
					{
						rowEndCommands.Add(
							new ClearGlobalVolumeSlideCommand());
					}

					AddRepeatCommand(repeatCommands, command);
					transformed?.Add(command);
					break;
			}
		}

		return new ResolvedCommands
		{
			Commands = transformed ?? noteEvent.Commands,
			RowEndCommands = rowEndCommands,
			RepeatCommands = repeatCommands,
			Retrigger = retrigger,
			NoteCutTick = noteCutTick,
			NoteDelayTick = noteDelayTick,
		};
	}

	private static int GetFinePatternDelayTicksForRow(
		IReadOnlyList<WorkingEvent> events,
		int row)
	{
		int total = 0;

		foreach (WorkingEvent workingEvent in events)
		{
			if (FloorRow(workingEvent.RowOffset) != row)
				continue;

			foreach (NoteCommand command
				in workingEvent.NoteEvent.Commands)
			{
				if (command is not ApplyTrackerFinePatternDelayCommand delay)
					continue;

				if (workingEvent.NoteEvent.Target.Kind
					!= ChannelTargetKind.Physical)
				{
					throw new InvalidOperationException(
						"Tracker fine pattern delay requires a physical channel target.");
				}

				total = checked(total + delay.ExtraTicks);
			}
		}

		return total;
	}

	private static byte GetPatternDelayRowsForRow(
		IReadOnlyList<WorkingEvent> events,
		int row)
	{
		WorkingEvent? selected = null;
		byte extraRows = 0;

		foreach (WorkingEvent workingEvent in events)
		{
			if (workingEvent.AffectsTiming)
				continue;
			if (FloorRow(workingEvent.RowOffset) != row)
				continue;

			foreach (NoteCommand command in workingEvent.NoteEvent.Commands)
			{
				if (command is not ApplyTrackerPatternDelayCommand delay)
					continue;

				if (workingEvent.NoteEvent.Target.Kind
					!= ChannelTargetKind.Physical)
				{
					throw new InvalidOperationException(
						"Tracker pattern delay requires a physical channel target.");
				}

				if (selected is null
					|| ComparePatternDelaySources(
						workingEvent,
						selected) < 0)
				{
					selected = workingEvent;
					extraRows = delay.ExtraRows;
				}

				break;
			}
		}

		return extraRows;
	}

	private static int ComparePatternDelaySources(
		WorkingEvent left,
		WorkingEvent right)
	{
		int compare = CompareTargets(
			left.NoteEvent.Target,
			right.NoteEvent.Target);
		if (compare != 0)
			return compare;

		return left.NoteEvent.EmissionOrder.CompareTo(
			right.NoteEvent.EmissionOrder);
	}

	private static bool HasEffectColumnAbsolutePanning(
		IReadOnlyList<NoteCommand> commands)
	{
		foreach (NoteCommand command in commands)
		{
			if (command is ApplyTrackerPanningCommand
				or ApplyTrackerPanning8BitCommand
				or SetSurroundCommand { Enabled: true })
			{
				return true;
			}
		}

		return false;
	}

	private static float TrackerPan64ToSpatialX(int trackerPan)
	{
		if (trackerPan < 0 || trackerPan > 64)
			throw new ArgumentOutOfRangeException(nameof(trackerPan));

		return (trackerPan - 32) / 32.0f;
	}

	internal static NoteCommand? ApplyRowTickOverride(
		NoteCommand? command,
		int? rowTicksOverride)
	{
		if (command is null || !rowTicksOverride.HasValue)
			return command;

		return command switch
		{
			SetPitchSlideCommand slide =>
				slide with { TicksPerRow = rowTicksOverride.Value },
			SetNoteVolumeSlideCommand slide =>
				slide with { TicksPerRow = rowTicksOverride.Value },
			SetSpatialXSlideCommand slide =>
				slide with { TicksPerRow = rowTicksOverride.Value },
			SetOverallChannelVolumeSlideCommand slide =>
				slide with { TicksPerRow = rowTicksOverride.Value },
			SetGlobalVolumeSlideCommand slide =>
				slide with { TicksPerRow = rowTicksOverride.Value },
			SetTonePortamentoCommand portamento =>
				portamento with { TicksPerRow = rowTicksOverride.Value },
			SetTremorCommand tremor =>
				tremor with { TicksPerRow = rowTicksOverride.Value },
			SetPanbrelloCommand panbrello =>
				panbrello with { TicksPerRow = rowTicksOverride.Value },
			_ => command,
		};
	}

	private static void AddRepeatCommand(
		List<NoteCommand> repeatCommands,
		NoteCommand command)
	{
		switch (command)
		{
			case SetPitchSlideCommand _:
			case SetNoteVolumeSlideCommand _:
			case AdjustPitchLinearUnitsCommand _:
			case AdjustNoteVolumeCommand _:
			case SetSpatialXSlideCommand _:
			case AdjustSpatialXCommand _:
			case SetOverallChannelVolumeSlideCommand _:
			case AdjustOverallChannelVolumeCommand _:
			case SetGlobalVolumeSlideCommand _:
			case AdjustGlobalVolumeCommand _:
			case SetTremorCommand _:
			case SetPanbrelloCommand _:
				repeatCommands.Add(command);
				break;

			case SetTonePortamentoCommand tonePortamento:
				repeatCommands.Add(
					tonePortamento with { TargetNote = null });
				break;
		}
	}

	private static void ExpandRetrigger(
		List<NoteEvent> resolved,
		NoteEvent sourceEvent,
		RetriggerRequest request,
		double eventTickPosition,
		double fixedTimeOffsetSeconds,
		double rowStartSeconds,
		double rowEndSeconds,
		int tickSpan,
		RowTickTimeline tickTimeline,
		SequencingContext context)
	{
		byte volumeTransform =
			(byte)(request.Parameter >> 4);
		int intervalTicks =
			request.Parameter & 0x0F;
		int countdown = request.HasNewNote
			? intervalTicks
			: request.ChannelState.RetriggerCountdown;
		int firstTick =
			request.HasNewNote ? 1 : 0;

		for (int tick = firstTick;
			tick < tickSpan;
			tick++)
		{
			double retriggerTickPosition =
				eventTickPosition + tick;
			if (retriggerTickPosition
				>= tickTimeline.EndTickPosition + 1e-9)
			{
				break;
			}

			double retriggerTimeSeconds =
				rowStartSeconds
					+ tickTimeline.GetSecondsAtTickPosition(
						retriggerTickPosition)
					+ fixedTimeOffsetSeconds;
			if (retriggerTimeSeconds >= rowEndSeconds)
				break;

			countdown--;
			if (countdown > 0)
				continue;

			resolved.Add(
				new NoteEvent(
					new MusicalTime(
						TimeSpanFromSeconds(
							retriggerTimeSeconds),
						0.0),
					context.MapTarget(sourceEvent.Target),
					new NoteCommand[]
						{
							new RetriggerCurrentVoiceCommand(
								volumeTransform),
						},
					SyntheticOrder(
						sourceEvent.EmissionOrder,
						3)));

			countdown = intervalTicks;
		}

		request.ChannelState.RetriggerCountdown =
			Math.Clamp(countdown, 0, 15);
	}

	private static bool HasStartNote(IReadOnlyList<NoteCommand> commands)
	{
		foreach (NoteCommand command in commands)
		{
			if (command is StartNoteCommand)
				return true;
		}

		return false;
	}

	private static SequencingChannelState GetTrackerChannelState(
		NoteEvent noteEvent,
		SequencingContext context,
		string effectName)
	{
		if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
		{
			throw new InvalidOperationException(
				$"{effectName} requires a physical channel target.");
		}

		return context.GetPhysicalChannelState(
			noteEvent.Target.PhysicalChannel);
	}

	private static byte ResolveVolumeColumnTonePortamentoSpeed(byte parameter)
		=> parameter switch
		{
			0 => 0,
			1 => 1,
			2 => 4,
			3 => 8,
			4 => 16,
			5 => 32,
			6 => 64,
			7 => 96,
			8 => 128,
			9 => 255,
			_ => throw new ArgumentOutOfRangeException(nameof(parameter)),
		};

	private static NoteCommand? ResolveTrackerVolumeSlide(byte parameter)
	{
		if (parameter == 0)
			return null;

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		if (low == 0)
			return new SetNoteVolumeSlideCommand(high);

		if (high == 0)
			return new SetNoteVolumeSlideCommand(-low);

		if (low == 0x0F)
			return new AdjustCurrentNoteVolumeCommand(high);

		if (high == 0x0F)
			return new AdjustCurrentNoteVolumeCommand(-low);

		return null;
	}

	private static NoteCommand? ResolveTrackerGlobalVolumeSlide(
		byte parameter)
	{
		if (parameter == 0)
			return null;

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		// Fine slide up has priority, so WFF is +15 like Impulse Tracker.
		if (low == 0x0F && high != 0)
			return new AdjustGlobalVolumeCommand(high);

		if (high == 0x0F && low != 0)
			return new AdjustGlobalVolumeCommand(-low);

		// For regular Wxy, IT gives the high nibble priority.
		if (high != 0)
			return new SetGlobalVolumeSlideCommand(high);

		return low == 0
			? null
			: new SetGlobalVolumeSlideCommand(-low);
	}

	private static NoteCommand? ResolveTrackerChannelVolumeSlide(
		byte parameter)
	{
		if (parameter == 0)
			return null;

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		// Fine slide up has priority, so NFF is +15 like Impulse Tracker.
		if (low == 0x0F && high != 0)
			return new AdjustOverallChannelVolumeCommand(high);

		if (high == 0x0F && low != 0)
			return new AdjustOverallChannelVolumeCommand(-low);

		// For ordinary Nxy with both nibbles non-zero, IT gives the low
		// nibble priority and slides down.
		if (low != 0)
			return new SetOverallChannelVolumeSlideCommand(-low);

		return high == 0
			? null
			: new SetOverallChannelVolumeSlideCommand(high);
	}

	private static NoteCommand? ResolveTrackerPanningSlide(
		byte parameter)
	{
		if (parameter == 0)
			return null;

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		const double spatialUnitsPerTrackerPanStep =
			4.0 / 128.0;

		if (low == 0)
		{
			return new SetSpatialXSlideCommand(
				-high * spatialUnitsPerTrackerPanStep,
				MinimumX: -1.0,
				MaximumX: 1.0);
		}

		if (high == 0)
		{
			return new SetSpatialXSlideCommand(
				low * spatialUnitsPerTrackerPanStep,
				MinimumX: -1.0,
				MaximumX: 1.0);
		}

		if (low == 0x0F)
		{
			return new AdjustSpatialXCommand(
				-high * spatialUnitsPerTrackerPanStep,
				MinimumX: -1.0,
				MaximumX: 1.0);
		}

		if (high == 0x0F)
		{
			return new AdjustSpatialXCommand(
				low * spatialUnitsPerTrackerPanStep,
				MinimumX: -1.0,
				MaximumX: 1.0);
		}

		// Impulse Tracker ignores ordinary Pxy slides when both nibbles
		// are non-zero.
		return null;
	}

	private static NoteCommand? ResolveTrackerPitchSlide(
		byte parameter,
		double direction)
	{
		if (parameter == 0)
			return null;

		byte high = (byte)(parameter >> 4);
		byte low = (byte)(parameter & 0x0F);

		if (high == 0x0E)
		{
			return low == 0
				? null
				: new AdjustPitchLinearUnitsCommand(direction * low);
		}

		if (high == 0x0F)
		{
			return low == 0
				? null
				: new AdjustPitchLinearUnitsCommand(direction * low * 4.0);
		}

		return new SetPitchSlideCommand(
			direction * parameter * 4.0);
	}

	private static List<NoteCommand> CopyCommandsBefore(
		IReadOnlyList<NoteCommand> commands,
		int count)
	{
		List<NoteCommand> result = new(count);
		for (int i = 0; i < count; i++)
			result.Add(commands[i]);
		return result;
	}

	private static TimeSpan TimeSpanFromSeconds(double seconds)
	{
		if (double.IsNaN(seconds)
			|| double.IsInfinity(seconds))
		{
			throw new ArgumentOutOfRangeException(nameof(seconds));
		}

		double ticks = seconds * TimeSpan.TicksPerSecond;
		if (ticks < long.MinValue || ticks > long.MaxValue)
			throw new OverflowException("Time value is outside the TimeSpan range.");

		return TimeSpan.FromTicks(
			checked((long)Math.Round(
				ticks,
				MidpointRounding.AwayFromZero)));
	}

	private static NoteEvent ResolveAt(
		NoteEvent noteEvent,
		IReadOnlyList<NoteCommand> commands,
		double timeSeconds,
		SequencingContext context,
		long emissionOrder)
		=> noteEvent with
		{
			Offset = new MusicalTime(TimeSpanFromSeconds(timeSeconds), 0.0),
			Target = context.MapTarget(noteEvent.Target),
			Commands = commands,
			EmissionOrder = emissionOrder,
		};

	private static int EffectiveSCommandTick(byte tick)
		=> Math.Max(1, (int)tick);

	private static long SyntheticOrder(long emissionOrder, int phase)
		=> checked(emissionOrder * 8 + phase);

	private static int CompareTimingEvents(WorkingEvent left, WorkingEvent right)
	{
		int compare = left.TimingEligibilitySeconds!.Value.CompareTo(
			right.TimingEligibilitySeconds!.Value);
		if (compare != 0)
			return compare;

		if (left.NoteEvent.Target.Kind == ChannelTargetKind.Physical
			&& right.NoteEvent.Target.Kind == ChannelTargetKind.Physical)
		{
			compare = left.NoteEvent.Target.PhysicalChannel.CompareTo(
				right.NoteEvent.Target.PhysicalChannel);
			if (compare != 0)
				return compare;
		}

		return left.NoteEvent.EmissionOrder.CompareTo(
			right.NoteEvent.EmissionOrder);
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
