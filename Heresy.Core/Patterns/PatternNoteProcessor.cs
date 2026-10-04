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

	private sealed class RetriggerRequest
	{
		public required SequencingChannelState ChannelState { get; init; }
		public required byte Parameter { get; init; }
		public required bool HasNewNote { get; init; }
	}

	private sealed class ResolvedCommands
	{
		public required IReadOnlyList<NoteCommand> Commands { get; init; }
		public required IReadOnlyList<NoteCommand> RowEndCommands { get; init; }
		public RetriggerRequest? Retrigger { get; init; }
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
				resolved.Add(ResolveAt(
					workingEvent.NoteEvent,
					workingEvent.NoteEvent.Commands,
					rowStartSeconds,
					context,
					SyntheticOrder(workingEvent.NoteEvent.EmissionOrder, 0)));
				deferredTimingEvents.Remove(workingEvent);
			}

			double rowDurationSeconds = GetRowDurationSeconds(context.State);
			double rowEnd = Math.Min(row + 1.0, effectiveRowCount);
			double rowFraction = rowEnd - row;
			double rowEndSeconds = rowStartSeconds + rowDurationSeconds * rowFraction;

			foreach (WorkingEvent workingEvent in events)
			{
				if (workingEvent.AffectsTiming)
					continue;

				bool isFinalEndpoint =
					row == wholeRowCount - 1
					&& workingEvent.RowOffset == effectiveRowCount;

				if (workingEvent.RowOffset < row)
					continue;
				if (!isFinalEndpoint && workingEvent.RowOffset >= row + 1.0)
					continue;

				double fraction = workingEvent.RowOffset - row;
				if (fraction > rowFraction)
					continue;

				double eventTimeSeconds =
					rowStartSeconds
					+ fraction * rowDurationSeconds
					+ workingEvent.TimeOffsetSeconds;

				ResolvedCommands commands = ResolveCommands(
					workingEvent.NoteEvent,
					context);

				if (commands.Commands.Count != 0)
				{
					resolved.Add(ResolveAt(
						workingEvent.NoteEvent,
						commands.Commands,
						eventTimeSeconds,
						context,
						SyntheticOrder(workingEvent.NoteEvent.EmissionOrder, 0)));
				}

				if (commands.Retrigger is not null)
				{
					ExpandRetrigger(
						resolved,
						workingEvent.NoteEvent,
						commands.Retrigger,
						eventTimeSeconds,
						rowEndSeconds,
						context.State,
						context);
				}

				if (commands.RowEndCommands.Count != 0)
				{
					double clearTimeSeconds = Math.Max(rowEndSeconds, eventTimeSeconds);
					resolved.Add(new NoteEvent(
						new MusicalTime(TimeSpan.FromSeconds(clearTimeSeconds), 0.0),
						context.MapTarget(workingEvent.NoteEvent.Target),
						commands.RowEndCommands,
						SyntheticOrder(workingEvent.NoteEvent.EmissionOrder, 1)));
				}
			}

			rowStartSeconds = rowEndSeconds;
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

	private static ResolvedCommands ResolveCommands(
		NoteEvent noteEvent,
		SequencingContext context)
	{
		List<NoteCommand>? transformed = null;
		List<NoteCommand> rowEndCommands = [];
		RetriggerRequest? retrigger = null;

		for (int i = 0; i < noteEvent.Commands.Count; i++)
		{
			NoteCommand command = noteEvent.Commands[i];

			switch (command)
			{
				case ApplyVibratoCommand vibrato:
				{
					SequencingChannelState channelState =
						GetTrackerChannelState(noteEvent, context, "Tracker vibrato");
					transformed ??= CopyCommandsBefore(noteEvent.Commands, i);

					byte parameter = channelState.ResolveEffectParameterNibbles(
						EffectMemorySlot.Vibrato,
						vibrato.Parameter);

					transformed.Add(new SetVibratoCommand(
						(byte)(parameter >> 4),
						(byte)(parameter & 0x0F)));
					rowEndCommands.Add(new ClearPitchModulationCommand());
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

					NoteCommand? resolved = ResolveTrackerVolumeSlide(parameter);
					if (resolved is not null)
					{
						transformed.Add(resolved);
						if (resolved is SetNoteVolumeSlideCommand)
							rowEndCommands.Add(new ClearNoteVolumeSlideCommand());
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

					NoteCommand? resolved = ResolveTrackerPitchSlide(
						parameter,
						direction: -1.0);
					if (resolved is not null)
					{
						transformed.Add(resolved);
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

					NoteCommand? resolved = ResolveTrackerPitchSlide(
						parameter,
						direction: 1.0);
					if (resolved is not null)
					{
						transformed.Add(resolved);
						if (resolved is SetPitchSlideCommand)
							rowEndCommands.Add(new ClearPitchSlideCommand());
					}
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
						transformed.Add(new SetTonePortamentoCommand(
							parameter * 4.0,
							tonePortamento.TargetNote));
						rowEndCommands.Add(new ClearTonePortamentoCommand());
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
						(byte)(parameter & 0x0F)));
					rowEndCommands.Add(new ClearTremoloCommand());
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

					bool hasNewNote = false;
					foreach (NoteCommand eventCommand in noteEvent.Commands)
					{
						if (eventCommand is StartNoteCommand)
						{
							hasNewNote = true;
							break;
						}
					}

					retrigger = new RetriggerRequest
					{
						ChannelState = channelState,
						Parameter = parameter,
						HasNewNote = hasNewNote,
					};
					break;
				}

				default:
					if (command is SetPitchSlideCommand)
						rowEndCommands.Add(new ClearPitchSlideCommand());

					if (command is SetNoteVolumeSlideCommand)
						rowEndCommands.Add(new ClearNoteVolumeSlideCommand());

					transformed?.Add(command);
					break;
			}
		}

		return new ResolvedCommands
		{
			Commands = transformed ?? noteEvent.Commands,
			RowEndCommands = rowEndCommands,
			Retrigger = retrigger,
		};
	}

	private static void ExpandRetrigger(
		List<NoteEvent> resolved,
		NoteEvent sourceEvent,
		RetriggerRequest request,
		double eventTimeSeconds,
		double rowEndSeconds,
		SequencingState state,
		SequencingContext context)
	{
		byte volumeTransform = (byte)(request.Parameter >> 4);
		int intervalTicks = request.Parameter & 0x0F;
		int countdown = request.HasNewNote
			? intervalTicks
			: request.ChannelState.RetriggerCountdown;
		int firstTick = request.HasNewNote ? 1 : 0;

		double tickDurationSeconds =
			SequencingConstants.Diachron.TotalSeconds / state.Tempo;

		for (int tick = firstTick; tick < state.Speed; tick++)
		{
			double retriggerTimeSeconds =
				eventTimeSeconds + tick * tickDurationSeconds;
			if (retriggerTimeSeconds >= rowEndSeconds)
				break;

			countdown--;
			if (countdown > 0)
				continue;

			resolved.Add(new NoteEvent(
				new MusicalTime(
					TimeSpan.FromSeconds(retriggerTimeSeconds),
					0.0),
				context.MapTarget(sourceEvent.Target),
				new NoteCommand[]
				{
					new RetriggerCurrentVoiceCommand(volumeTransform),
				},
				SyntheticOrder(sourceEvent.EmissionOrder, 1)));

			countdown = intervalTicks;
		}

		request.ChannelState.RetriggerCountdown =
			Math.Clamp(countdown, 0, 15);
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
			return new AdjustNoteVolumeCommand(high);

		if (high == 0x0F)
			return new AdjustNoteVolumeCommand(-low);

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

	private static NoteEvent ResolveAt(
		NoteEvent noteEvent,
		IReadOnlyList<NoteCommand> commands,
		double timeSeconds,
		SequencingContext context,
		long emissionOrder)
		=> noteEvent with
		{
			Offset = new MusicalTime(TimeSpan.FromSeconds(timeSeconds), 0.0),
			Target = context.MapTarget(noteEvent.Target),
			Commands = commands,
			EmissionOrder = emissionOrder,
		};

	private static long SyntheticOrder(long emissionOrder, int phase)
		=> checked(emissionOrder * 2 + phase);

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
