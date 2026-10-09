using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Patterns;

/// <summary>
/// A single chronological observation from the experimental shared-tick
/// Pattern cursor merger. Advances yield control without synthesizing notes.
/// </summary>
public abstract record IncrementalPatternTimelineStep(double Tick, TimeSpan Time)
{
	public sealed record Emit(NoteEvent Note, double Tick, TimeSpan Time)
		: IncrementalPatternTimelineStep(Tick, Time)
	{
		/// <summary>Producer invocation of this event. The recursive
		/// coordinator uses it for per-invocation child ownership.</summary>
		public long InvocationId { get; init; } = -1;
	}

	public sealed record Advance(double Tick, TimeSpan Time)
		: IncrementalPatternTimelineStep(Tick, Time);

	/// <summary>
	/// The raw producer voluntarily returned CPU control before completing
	/// its current row. No musical time advances, no note is emitted, and
	/// no command from the unfinished row has been interpreted yet.
	/// </summary>
	public sealed record Cooperate(
		long InvocationId, double Tick, TimeSpan Time)
		: IncrementalPatternTimelineStep(Tick, Time);

	/// <summary>
	/// The invocation completed its terminating Bxx/Cxx source row. The
	/// surrounding Sequence interprets the order jump and next-pattern
	/// start row; this timeline does not execute a Sequence order.
	/// </summary>
	public sealed record Flow(
		long InvocationId, PatternFlowControl Control,
		double Tick, TimeSpan Time)
		: IncrementalPatternTimelineStep(Tick, Time);
}

/// <summary>
/// Experimental *within-row* scheduler for cooperative raw Pattern generators.
/// All active invocations advance on one shared tracker-tick clock, with their
/// individual row-end ticks captured independently. A cursor processes only
/// its due operation and retains its raw enumerator between operations.
///
/// This prototype admits only ordinary Note Start/Off/Cut, data Source-column
/// selection, and standalone global Tempo/Speed. Unsupported commands and
/// wall-time offsets fail explicitly; advanced tracker effects still use the
/// established production PatternNoteProcessor/scheduler.
/// </summary>
public sealed class IncrementalPatternTimeline : IDisposable
{
	private const int MaximumRawStepsPerRow = 8192;
	private const int MaximumOperationsAtOneTick = 8192;
	private const double TickTolerance = 1e-9;

	private sealed class SingleEventSlice(NoteEvent note) : IDeferredSourcePatternGenerator
	{
		public void GenerateRawNotes(
			SequencingContext context, INoteReceiver output, out double rowCount)
		{
			output.Append(note with
			{
				Offset = new MusicalTime(TimeSpan.Zero, 0),
			});
			rowCount = 1.0;
		}
	}

	private sealed record DeferredTiming(
		NoteEvent Raw, TimeSpan EligibleAt, long Order);

	private enum TickOperationKind { DeferredRaw, Cut, Retrigger, RepeatedDelayed }

	private sealed record TickOperation(
		double Offset, NoteEvent Note, TickOperationKind Kind,
		long Order, int Interval = 0, byte VolumeTransform = 0);

	private sealed class Cursor : IDisposable
	{
		// Row preparation is intentionally resumable. An iterator may
		// cooperate after emitting some raw commands, but no row-level
		// tracker effect can run until all due raw steps are collected.
		private sealed class RowPreparation
		{
			public List<NoteEvent> Events { get; } = [];
			public List<(int Channel, int Order, byte ExtraRows)> RowDelays { get; } = [];
			public int SourceOrder { get; set; }
			public int Inspected { get; set; }
		}

		private sealed class PatternLoopState(int startRow)
		{
			public int StartRow { get; set; } = startRow;
			public byte RemainingRepeats { get; set; }
		}

		private readonly IIncrementalRawPatternNoteGenerator _generator;
		private IEnumerator<RawPatternStep> _source;
		private readonly Dictionary<int, PatternLoopState> _loopStates = [];
		private readonly List<(int Channel, int Order, byte Count)> _rowLoopCommands = [];
		private readonly List<(int Channel, int Order, NoteCommand Command)> _rowFlowCommands = [];
		private readonly int _initialRow;
		private readonly List<DeferredTiming> _pendingTiming = [];
		private readonly List<DeferredTiming> _readyTiming = [];
		private long _nextTimingOrder;
		private RawPatternStep? _lookahead;
		private bool _ended;
		private double _lastRow;
		private IReadOnlyList<NoteEvent> _rowEvents = [];
		private readonly List<NoteEvent> _rowEndCommands = [];
		private readonly List<NoteEvent> _deferredLoopCleanup = [];
		private readonly List<NoteEvent> _wrapCleanup = [];
		private readonly List<(double TickOffset, NoteEvent Note)> _repeated = [];
		private readonly List<TickOperation> _scheduled = [];
		private long _scheduledOrder;
		private int _fineDelayTicks;
		private int _extraRowSpans;
		private int _eventIndex;
		private bool _inRow;
		private RowPreparation? _preparation;
		private TimeSpan _rowStartTime;
		private double _rowStartTick;
		private double _rowSpeed;

		public Cursor(
			IIncrementalRawPatternNoteGenerator generator,
			SequencingContext context,
			int rowCount,
			int startRow,
			double tick,
			long sequence)
		{
			Context = context;
			RowCount = rowCount;
			_initialRow = Math.Min(rowCount, startRow);
			Row = _initialRow;
			DueTick = tick;
			Sequence = sequence;
			_generator = generator;
			_source = generator.EnumerateRawSteps(context).GetEnumerator();
		}

		public SequencingContext Context { get; }
		public int RowCount { get; }
		public int Row { get; private set; }
		public double DueTick { get; private set; }
		public long Sequence { get; }
		public bool Complete => Row >= RowCount;
		public bool InRow => _inRow;
		public double RowSpeed => _rowSpeed;
		public int FineDelayTicks => _fineDelayTicks;
		public int ExtraRowSpans => _extraRowSpans;
		public double EffectiveSpanTicks => _rowSpeed + _fineDelayTicks;
		public double TotalRowTicks => EffectiveSpanTicks * (_extraRowSpans + 1);
		public double RowEndTick => _rowStartTick + TotalRowTicks;
		public NoteEvent? DueRepeated => _inRow && _repeated.Count != 0
			&& (_eventIndex >= _rowEvents.Count
				|| _repeated[0].TickOffset < RawEventTickOffset - TickTolerance)
			&& (_scheduled.Count == 0
				|| _repeated[0].TickOffset <= _scheduled[0].Offset + TickTolerance)
			&& (_wrapCleanup.Count == 0 || _repeated[0].TickOffset <= TickTolerance)
			? _repeated[0].Note : null;
		public TickOperation? DueScheduled => _inRow && _scheduled.Count != 0
			&& (_eventIndex >= _rowEvents.Count
				|| _scheduled[0].Offset < RawEventTickOffset - TickTolerance)
			&& (_repeated.Count == 0
				|| _scheduled[0].Offset < _repeated[0].TickOffset - TickTolerance)
			&& (_wrapCleanup.Count == 0 || _scheduled[0].Offset <= TickTolerance)
			? _scheduled[0] : null;

		private void Schedule(double offset, NoteEvent note,
			TickOperationKind kind, int interval = 0, byte volumeTransform = 0)
		{
			_scheduled.Add(new TickOperation(offset, note, kind,
				_scheduledOrder++, interval, volumeTransform));
			_scheduled.Sort((a, b) =>
			{
				int order = a.Offset.CompareTo(b.Offset);
				if (order != 0)
					return order;
				// Eager SyntheticOrder emits delayed note setup before
				// same-tick Qxy retrigger, and SCx cut after both.
				int priority = Priority(a.Kind).CompareTo(Priority(b.Kind));
				return priority != 0 ? priority : a.Order.CompareTo(b.Order);
			});
			RefreshDue();
		}

		private static int Priority(TickOperationKind kind)
			=> kind switch
			{
				TickOperationKind.DeferredRaw => 1,
				TickOperationKind.RepeatedDelayed => 2,
				TickOperationKind.Retrigger => 3,
				TickOperationKind.Cut => 4,
				_ => 5,
			};

		public void QueueCut(NoteEvent note, byte cutTick)
		{
			int tick = Math.Max(1, (int)cutTick);
			double original = (note.Offset.RowOffset - Row) * RowSpeed;
			if (tick >= EffectiveSpanTicks || original + tick >= TotalRowTicks)
				return;
			Schedule(original + tick, note with
			{
				Commands = [new NoteCutCommand()],
			}, TickOperationKind.Cut);
		}

		public void QueueDelayed(NoteEvent note, byte delayTick)
		{
			int tick = Math.Max(1, (int)delayTick);
			double original = (note.Offset.RowOffset - Row) * RowSpeed;
			if (tick >= EffectiveSpanTicks || original + tick >= TotalRowTicks)
				return;
			Schedule(original + tick, note, TickOperationKind.DeferredRaw);
		}

		public void QueueDelayedCopies(TickOperation original, NoteEvent resolved)
		{
			for (int span = 1; span <= ExtraRowSpans; span++)
			{
				double tick = original.Offset + span * EffectiveSpanTicks;
				if (tick >= TotalRowTicks)
					break;
				Schedule(tick, resolved, TickOperationKind.RepeatedDelayed);
			}
		}

		public void QueueRetrigger(
			NoteEvent note, byte input, byte? delayTick = null,
			bool canExecute = true)
		{
			SequencingChannelState channel =
				Context.GetPhysicalChannelState(note.Target.PhysicalChannel);
			// Qxx memory is read when its original row event is
			// encountered, even if SDx later suppresses execution.
			byte parameter = channel.ResolveEffectParameter(
				EffectMemorySlot.Retrigger, input);
			if (!canExecute)
				return;
			int interval = parameter & 0x0F;
			byte transform = (byte)(parameter >> 4);
			bool startsNew = note.Commands.Any(c => c is StartNoteCommand);
			double original = (note.Offset.RowOffset - Row) * RowSpeed;
			if (delayTick.HasValue)
			{
				int offset = Math.Max(1, (int)delayTick.Value);
				// SEy does not rescue SDx whose offset is outside its
				// *original* captured row span.
				if (offset >= EffectiveSpanTicks
					|| original + offset >= TotalRowTicks)
					return;
				original += offset;
			}
			// Qxy's countdown starts when the delayed note executes;
			// its first candidate retrigger is the next tracker tick.
			// All work is still scheduled lazily against the shared clock.
			if (startsNew)
				channel.RetriggerCountdown = interval;
			for (int tick = startsNew ? 1 : 0; original + tick < TotalRowTicks; tick++)
				Schedule(original + tick, note, TickOperationKind.Retrigger,
					interval, transform);
		}

		public void ConsumeScheduled()
		{
			if (DueScheduled is null)
				throw new InvalidOperationException("No tracker tick operation is due.");
			_scheduled.RemoveAt(0);
			RefreshDue();
		}

		public NoteEvent? ExecuteRetriggerTick(TickOperation operation)
		{
			SequencingChannelState state = Context.GetPhysicalChannelState(
					operation.Note.Target.PhysicalChannel);
			int countdown = state.RetriggerCountdown - 1;
			bool trigger = countdown <= 0;
			state.RetriggerCountdown = Math.Clamp(
					trigger ? operation.Interval : countdown, 0, 15);
			if (!trigger)
				return null;
			return operation.Note with
			{
				Commands = [new RetriggerCurrentVoiceCommand(operation.VolumeTransform)],
			};
		}
		private double RawEventTickOffset => _eventIndex >= _rowEvents.Count
			? double.PositiveInfinity
			: (_rowEvents[_eventIndex].Offset.RowOffset - Row) * _rowSpeed;
		public void QueueRepeats(NoteEvent note, double originalTickOffset)
		{
			if (_extraRowSpans == 0)
				return;
			List<NoteCommand> repeating = [];
			foreach (NoteCommand command in note.Commands)
			{
				if (command is SetPitchSlideCommand or SetNoteVolumeSlideCommand
					or SetOverallChannelVolumeSlideCommand
					or SetGlobalVolumeSlideCommand or SetSpatialXSlideCommand
					or AdjustPitchLinearUnitsCommand or AdjustNoteVolumeCommand
					or AdjustOverallChannelVolumeCommand
					or AdjustGlobalVolumeCommand or AdjustSpatialXCommand)
					repeating.Add(command);
			}
			if (repeating.Count == 0)
				return;
			for (int span = 1; span <= _extraRowSpans; span++)
				_repeated.Add((span * EffectiveSpanTicks + originalTickOffset, note with
				{
					Commands = repeating.ToArray(),
				}));
			_repeated.Sort((a,b) => a.TickOffset.CompareTo(b.TickOffset));
			RefreshDue();
		}
		public void ConsumeRepeated()
		{
			if (DueRepeated is null)
				throw new InvalidOperationException("No repeated row command is due.");
			_repeated.RemoveAt(0);
			RefreshDue();
		}
		public NoteEvent? DueEvent =>
			_inRow && _eventIndex < _rowEvents.Count
				&& (_repeated.Count == 0
					|| RawEventTickOffset <= _repeated[0].TickOffset + TickTolerance)
			&& (_scheduled.Count == 0
					|| RawEventTickOffset <= _scheduled[0].Offset + TickTolerance)
			&& (_wrapCleanup.Count == 0 || RawEventTickOffset <= TickTolerance)
				? _rowEvents[_eventIndex] : null;
		public NoteEvent? DueTiming =>
			_inRow && _readyTiming.Count > 0
				? _readyTiming[0].Raw : null;
		public IReadOnlyList<DeferredTiming> ReadyTimings => _readyTiming;

		/// <summary>Complete a preceding row before deciding Tempo at
		/// the next row's exact shared tick. No musical command is due.</summary>
		public bool CanFinishRowAt(double tick)
			=> _inRow && Math.Abs(DueTick - tick) <= TickTolerance
				&& DueTiming is null && DueEvent is null
				&& DueRepeated is null && DueScheduled is null
				&& DueWrapCleanup is null && DueCleanup is null;
		public NoteEvent? DueCleanup =>
			_inRow && DueEvent is null && DueRepeated is null && DueScheduled is null
			&& _wrapCleanup.Count == 0 && _rowEndCommands.Count > 0
				? _rowEndCommands[0] : null;

		public NoteEvent? DueWrapCleanup => _inRow && _wrapCleanup.Count > 0
			&& DueTiming is null
			&& (_eventIndex >= _rowEvents.Count || RawEventTickOffset > TickTolerance)
			&& (_repeated.Count == 0 || _repeated[0].TickOffset > TickTolerance)
			&& (_scheduled.Count == 0 || _scheduled[0].Offset > TickTolerance)
			? _wrapCleanup[0] : null;

		public void ConsumeWrapCleanup()
		{
			if (DueWrapCleanup is null)
				throw new InvalidOperationException("No SBx wrap cleanup is due.");
			_wrapCleanup.RemoveAt(0);
			RefreshDue();
		}

		/// <summary>
		/// The eager loop expander retains the *original* row's emission
		/// ordering. At a backwards visit, the target row's tick-zero
		/// commands can therefore precede this row's ending cleanup.
		/// </summary>
		public void DeferCleanupForBackwardLoop()
		{
			if (_rowEndCommands.Count == 0 || _rowFlowCommands.Count != 0)
				return;
			bool backward = false;
			foreach (var instruction in _rowLoopCommands
				.OrderBy(x => x.Channel).ThenBy(x => x.Order))
			{
				if (instruction.Count == 0)
					continue;
				_loopStates.TryGetValue(instruction.Channel,
						out PatternLoopState? state);
				if (state is null || state.RemainingRepeats != 1)
					backward = true;
			}
			if (!backward)
				return;
			_deferredLoopCleanup.AddRange(_rowEndCommands);
			_rowEndCommands.Clear();
		}

		public void QueueCleanup(NoteEvent note)
		{
			if (!_inRow)
				throw new InvalidOperationException("No active row for cleanup.");
			_rowEndCommands.Add(note);
		}

		public void ConsumeCleanup()
		{
			if (DueCleanup is null)
				throw new InvalidOperationException("No due row cleanup.");
			_rowEndCommands.RemoveAt(0);
		}

		public bool BeginRow(double tick, TimeSpan now)
		{
			if (_inRow || Complete)
				throw new InvalidOperationException("Cursor row is not available.");

			if (_preparation is null)
			{
				_rowStartTick = tick;
				_rowStartTime = now;
				_rowSpeed = Context.State.Speed;
				_fineDelayTicks = 0;
				_extraRowSpans = 0;
				_repeated.Clear();
				_scheduled.Clear();
				_rowLoopCommands.Clear();
				_rowFlowCommands.Clear();
				_preparation = new RowPreparation();
			}
			else if (Math.Abs(tick - _rowStartTick) > TickTolerance
				|| now != _rowStartTime)
			{
				throw new InvalidOperationException(
					"A partially prepared Pattern row cannot move musical time.");
			}

			RowPreparation preparation = _preparation;
			while (TryPeek(out RawPatternStep? step))
			{
				if (step is RawPatternStep.Cooperate)
				{
					// Do not complete or commit the partial row. The
					// next TryStep resumes this exact enumerator position.
					_lookahead = null;
					return false;
				}
				if (++preparation.Inspected > MaximumRawStepsPerRow)
					throw new InvalidOperationException(
						"Raw Pattern iterator exceeded the per-row cooperation budget.");
				// The final row may also own events at exactly RowCount.
				bool endpoint = Row == RowCount - 1
					&& step!.Row == RowCount
					&& step is RawPatternStep.Emit;
				if (step!.Row >= Row + 1.0 && !endpoint)
					break;
				_lookahead = null;
				if (step is RawPatternStep.Emit emit && emit.Row >= Row)
				{
					// Delay effects modify the current row's tick span, not
					// its audible command stream. Strip them without calling
					// the eager processor or changing shared tracker state.
					List<NoteCommand> commands = [];
					foreach (NoteCommand command in emit.Note.Commands)
					{
						if (command is ApplyTrackerFinePatternDelayCommand fine)
						{
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new InvalidOperationException("S6x requires a physical channel.");
							_fineDelayTicks = checked(_fineDelayTicks + fine.ExtraTicks);
						}
						else if (command is ApplyTrackerPatternLoopCommand loop)
						{
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical
								|| emit.Note.Offset.TimeOffset != TimeSpan.Zero)
								throw new NotSupportedException(
									"SBx requires a physical target without a fixed wall offset.");
							if (emit.Row < RowCount)
								_rowLoopCommands.Add((Context.MapPhysicalChannel(
									emit.Note.Target.PhysicalChannel), preparation.SourceOrder, loop.RepeatCount));
						}
						else if (command is ApplyTrackerOrderJumpCommand
							or ApplyTrackerPatternBreakCommand)
						{
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new NotSupportedException(
									"Bxx/Cxx requires a physical tracker channel.");
							if (emit.Row < RowCount)
								_rowFlowCommands.Add((Context.MapPhysicalChannel(
									emit.Note.Target.PhysicalChannel), preparation.SourceOrder, command));
						}
						else if (command is ApplyTrackerPatternDelayCommand delay)
						{
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new InvalidOperationException("SEy requires a physical channel.");
							// SEy is a row-span control, independent of a
							// cell's fixed wall offset on its note commands.
							preparation.RowDelays.Add((Context.MapPhysicalChannel(
								emit.Note.Target.PhysicalChannel), preparation.SourceOrder, delay.ExtraRows));
						}
						else
							commands.Add(command);
					}
					preparation.SourceOrder++;
					if (commands.Count == 0)
						continue;
					NoteEvent filtered = emit.Note with { Commands = commands.ToArray() };
					// Tracker Txx changes the shared row-boundary clock even
					// when the same cell also contains a musical command.
					// Split it into a timing request and the original
					// ordinary command sequence, so all simultaneous Txx
					// instructions still enter one global arbitration.
					// The ordinary commands retain their exact musical
					// position and any fixed wall-time offset.
					if (filtered.Target.Kind == ChannelTargetKind.Physical
						&& filtered.Commands.Any(c => c is ApplyTrackerTempoCommand)
						&& filtered.Commands.Any(c => c is not ApplyTrackerTempoCommand))
					{
						NoteEvent timing = filtered with
						{
							Commands = filtered.Commands
								.Where(c => c is ApplyTrackerTempoCommand).ToArray(),
						};
						NoteEvent ordinary = filtered with
						{
							Commands = filtered.Commands
								.Where(c => c is not ApplyTrackerTempoCommand).ToArray(),
						};
						Validate(timing);
						Validate(ordinary);
						if (timing.Offset.RowOffset < RowCount)
							_pendingTiming.Add(new DeferredTiming(
								timing, now + timing.Offset.TimeOffset,
								_nextTimingOrder++));
						preparation.Events.Add(ordinary);
						continue;
					}
					Validate(filtered);
					if (IsTiming(filtered))
					{
						// Commands exactly at the Pattern endpoint have no
						// eligible source row. Do not apply them to the
						// preceding final row.
						if (filtered.Offset.RowOffset >= RowCount)
							continue;
						// Fractional timing positions apply at the start of
						// their nominal row. A positive fixed offset is an
						// eligibility deadline, never an execution timestamp.
						_pendingTiming.Add(new DeferredTiming(
							filtered, now + filtered.Offset.TimeOffset,
							_nextTimingOrder++));
					}
					else
					{
						preparation.Events.Add(filtered);
					}
				}
			}

			if (preparation.RowDelays.Count != 0)
				_extraRowSpans = preparation.RowDelays.OrderBy(x => x.Channel)
					.ThenBy(x => x.Order).First().ExtraRows;
			// Txx requests remain pending for shared-clock arbitration.
			// An SEy row's captured span and repeat count are retained
			// on this cursor for scheduled compatibility-row repeats.
			// Stable equal-time order: timing at the beginning of the row
			// (including fractional Timing commands) comes before notes;
			// then physical channels, finally the producer's emission order.
			_rowEvents = preparation.Events.Select((note, index) => (note, index))
				.OrderBy(x => DuePosition(x.note, Row, RowCount))
				.ThenBy(x => x.note.Target.Kind == ChannelTargetKind.Global
					? -1 : x.note.Target.PhysicalChannel)
				.ThenBy(x => x.index)
				.Select(x => x.note)
				.ToArray();
			_eventIndex = 0;
			_readyTiming.AddRange(_pendingTiming
				.Where(t => t.EligibleAt <= now)
				.OrderBy(t => t.EligibleAt)
				.ThenBy(t => t.Order));
			foreach (DeferredTiming timing in _readyTiming)
				_pendingTiming.Remove(timing);
			_inRow = true;
			_preparation = null;
			RefreshDue();
			return true;
		}

		private bool TryPeek(out RawPatternStep? result)
		{
			int discarded = 0;
			while (_lookahead is null && !_ended)
			{
				if (!_source.MoveNext())
				{
					_ended = true;
					break;
				}

				RawPatternStep step = _source.Current
					?? throw new InvalidOperationException("Raw Pattern emitted null.");
				if (!double.IsFinite(step.Row) || step.Row < 0)
					throw new InvalidOperationException(
						"Raw Pattern positions must be finite and nonnegative.");
				if (step is RawPatternStep.Emit && step.Row < _lastRow)
				{
					// The event is silent in playback, but diagnostic
					// messages are retained up to the context-wide cap.
					Context.Diagnostics.ReportDroppedOutOfOrderNote(
						step.Row, _lastRow);
					if (++discarded > MaximumRawStepsPerRow)
						throw new InvalidOperationException(
							"Raw Pattern iterator exceeded its no-progress budget.");
					continue;
				}
				if (step is not RawPatternStep.Cooperate
					&& step.Row < _lastRow)
					throw new InvalidOperationException(
						"Raw Pattern progress markers must be nondecreasing.");

				// Cooperation markers carry only an informational position.
				if (step is not RawPatternStep.Cooperate)
					_lastRow = step.Row;
				_lookahead = step;
			}
			result = _lookahead;
			return result is not null;
		}


		public void ConsumeEvent()
		{
			if (DueEvent is null)
				throw new InvalidOperationException("Cursor has no due event.");
			_eventIndex++;
			RefreshDue();
		}

		public void ConsumeTiming()
		{
			if (DueTiming is null)
				throw new InvalidOperationException("Cursor has no due timing command.");
			NoteEvent executed = _readyTiming[0].Raw;
			_readyTiming.RemoveAt(0);
			// A boundary Speed change resizes this cursor's own newly
			// started row, but not any other cursor's active row.
			if (executed.Commands.Any(c => c is SetSpeedCommand))
				_rowSpeed = Context.State.Speed;
			RefreshDue();
		}

		public PatternFlowControl FinishRow()
		{
			if (!_inRow || DueEvent is not null || DueRepeated is not null
				|| DueScheduled is not null)
				throw new InvalidOperationException("Cursor row is not finished.");

			int nextRow = Row + 1;
			int? firstActiveLoopChannel = null;
			foreach (var instruction in _rowLoopCommands
				.OrderBy(x => x.Channel).ThenBy(x => x.Order))
			{
				if (!_loopStates.TryGetValue(instruction.Channel,
					out PatternLoopState? loopState))
				{
					loopState = new PatternLoopState(_initialRow);
					_loopStates.Add(instruction.Channel, loopState);
				}
				if (instruction.Count == 0)
				{
					loopState.StartRow = Row;
					continue;
				}
				if (loopState.RemainingRepeats == 0)
				{
					loopState.RemainingRepeats = instruction.Count;
					nextRow = loopState.StartRow;
					firstActiveLoopChannel ??= instruction.Channel;
				}
				else
				{
					loopState.RemainingRepeats--;
					if (loopState.RemainingRepeats != 0)
					{
						nextRow = loopState.StartRow;
						firstActiveLoopChannel ??= instruction.Channel;
					}
					else
						loopState.StartRow = Row + 1;
				}
			}

			byte? order = null, breakRow = null;
			foreach (var instruction in _rowFlowCommands
				.OrderBy(x => x.Order).ThenBy(x => x.Channel))
			{
				switch (instruction.Command)
				{
					case ApplyTrackerOrderJumpCommand jump:
						order = jump.Order;
						break;
					case ApplyTrackerPatternBreakCommand patternBreak
						when !firstActiveLoopChannel.HasValue
							|| instruction.Channel < firstActiveLoopChannel.Value:
						breakRow = patternBreak.Row;
						break;
				}
			}
			PatternFlowControl flow = new(order, breakRow)
			{
				SourceRow = order.HasValue || breakRow.HasValue ? Row : null,
			};

			// It is never legal to replay source code with invocation-local
			// side effects. Only explicitly replay-safe generators can be
			// restarted for backward SBx row visits.
			if (!flow.HasControl && nextRow <= Row
				&& _generator is not IReplayableRawPatternNoteGenerator)
				throw new NotSupportedException(
					"SBx backwards loops require a replay-safe raw Pattern generator.");

			_inRow = false;
			_rowEvents = [];
			_rowEndCommands.Clear();
			_repeated.Clear();
			_scheduled.Clear();
			_readyTiming.Clear();
			_rowLoopCommands.Clear();
			_rowFlowCommands.Clear();
			if (flow.HasControl)
			{
				_pendingTiming.Clear();
				_deferredLoopCleanup.Clear();
				_wrapCleanup.Clear();
			}
			else if (nextRow <= Row)
			{
				_wrapCleanup.AddRange(_deferredLoopCleanup);
				_deferredLoopCleanup.Clear();
			}
			if (flow.HasControl)
				Row = RowCount;
			else if (nextRow <= Row)
			{
				_source.Dispose();
				_source = _generator.EnumerateRawSteps(Context).GetEnumerator();
				_lookahead = null;
				_ended = false;
				_lastRow = 0;
				Row = nextRow;
			}
			else
				Row = nextRow;
			return flow;
		}

		private void RefreshDue()
		{
			if (DueTiming is not null)
			{
				DueTick = _rowStartTick;
				return;
			}
			double rawTick = _eventIndex < _rowEvents.Count
				? (DuePosition(_rowEvents[_eventIndex], Row, RowCount) - Row) * _rowSpeed
				: TotalRowTicks;
			double repeatTick = _repeated.Count == 0
				? double.PositiveInfinity : _repeated[0].TickOffset;
			double commandTick = _scheduled.Count == 0
				? double.PositiveInfinity : _scheduled[0].Offset;
			double wrapTick = _wrapCleanup.Count == 0
				? double.PositiveInfinity : 0;
			DueTick = _rowStartTick + Math.Min(wrapTick,
				Math.Min(rawTick, Math.Min(repeatTick, commandTick)));
		}

		public void Dispose() => _source.Dispose();

		private static double DuePosition(NoteEvent note, int row, int rowCount)
			=> IsTiming(note) && note.Offset.RowOffset < rowCount
				? Math.Floor(note.Offset.RowOffset)
				: note.Offset.RowOffset;

		private static bool IsTiming(NoteEvent note)
			=> note.Commands.Count != 0
				&& (note.Target.Kind == ChannelTargetKind.Global
					&& note.Commands.All(c => c is SetTempoCommand or SetSpeedCommand)
					|| note.Target.Kind == ChannelTargetKind.Physical
						&& note.Commands.Count == 1
						&& note.Commands[0] is ApplyTrackerTempoCommand);

		private static void Validate(NoteEvent note)
		{
			if (note.Offset.TimeOffset < TimeSpan.Zero)
				throw new NotSupportedException(
					"The incremental tick merger does not support negative wall-time offsets.");
			if (note.Offset.TimeOffset > TimeSpan.Zero
				&& !IsTiming(note)
				&& (note.Target.Kind != ChannelTargetKind.Physical
					|| note.Commands.Any(c => c is not (StartNoteCommand
						or NoteOffCommand or NoteCutCommand
						or ApplyTrackerNoteCutCommand
						or ApplyTrackerNoteDelayCommand
						or ApplyRetriggerCommand))))
				throw new NotSupportedException(
					"Positive fixed wall-time offsets require standalone global Tempo/Speed or supported physical Note/Off/Cut/SCx/SDx/Qxy.");
			if (note.Target.Kind is not (ChannelTargetKind.Physical
				or ChannelTargetKind.Global))
				throw new NotSupportedException(
					"The incremental tick merger does not yet support virtual targets.");

			foreach (NoteCommand command in note.Commands)
			{
				bool allowed = note.Target.Kind == ChannelTargetKind.Global
					? command is SetTempoCommand or SetSpeedCommand
					: command is StartNoteCommand or NoteOffCommand
						or NoteCutCommand or SelectPatternSourceCommand
						or SetPitchSlideCommand or SetNoteVolumeSlideCommand
						or ApplyVolumeSlideCommand or ApplyPitchSlideDownCommand
						or ApplyPitchSlideUpCommand or ApplyChannelVolumeSlideCommand
						or ApplyGlobalVolumeSlideCommand or ApplyPanningSlideCommand
						or SetOverallChannelVolumeSlideCommand
						or SetGlobalVolumeSlideCommand or SetSpatialXSlideCommand
						or ApplyTrackerTempoCommand or ApplyTrackerNoteCutCommand
						or ApplyTrackerNoteDelayCommand or ApplyRetriggerCommand
						or SetNoteVolumeCommand or ApplySampleOffsetCommand;
				if (!allowed)
					throw new NotSupportedException(
						$"The incremental tick merger does not yet support {command.GetType().Name}.");
			}

			if (!double.IsFinite(note.Offset.RowOffset)
				|| note.Offset.RowOffset < 0)
				throw new InvalidOperationException("Invalid raw musical position.");
		}
	}

	private sealed record DeferredNote(
		Cursor Owner, NoteEvent Raw, TimeSpan Deadline, long Order,
		bool IsResolved = false, double? RowEndTick = null);

	/// <summary>
	/// A tracker Tempo ramp is linear in musical tick position, never wall
	/// time. Its starting Tempo is the *actual* shared Tempo at its boundary.
	/// </summary>
	private sealed record TempoBoundarySet(
		double Tempo, ChannelTarget Target, long InvocationId);

	// Each simultaneous SEy source retains its own row speed, repetition
	// count, mapped physical target, and cancellation ownership. The combined
	// ramp is a projection of these sources, never a pre-executed row.
	private sealed record RepeatedTempoSource(
		long InvocationId, byte Parameter, int Span, int Repeats,
		int Channel, ChannelTarget Target, long SourceOrder,
		double OriginTick)
	{
		public double EndTick => OriginTick + (double)Span * (Repeats + 1);

		public bool RepeatsAt(double tick)
		{
			double offset = tick - OriginTick;
			if (offset <= TickTolerance || tick >= EndTick - TickTolerance)
				return false;
			double repetition = offset / Span;
			return Math.Abs(repetition - Math.Round(repetition)) <= TickTolerance;
		}
	}

	private sealed record ActiveTempoRamp(
		double StartTick, double EndTick,
		double StartTempo, double EndTempo,
		IReadOnlyList<TempoBoundarySet>? BoundaryTempoSets = null)
	{
		public double TempoAt(double tick)
			=> StartTempo + (EndTempo - StartTempo)
				* Math.Clamp((tick - StartTick) / (EndTick - StartTick), 0, 1);
	}

	private readonly SequencingContext _root;
	private readonly List<Cursor> _active = [];
	private readonly List<DeferredNote> _delayed = [];
	private long _nextDeferredOrder;
	private ActiveTempoRamp? _tempoRamp;
	private readonly Queue<ActiveTempoRamp> _futureTempoRamps = new();
	private long _futureTempoOwner = -1;
	private readonly List<RepeatedTempoSource> _crossTempoSources = [];
	private bool _crossTempoBoundaryPending;
	private readonly Queue<IncrementalPatternTimelineStep.Emit> _queuedTempoEvents = new();
	private long _nextSequence;
	private long _emissionOrder;
	private double _tick;
	private double _lastStepTick = double.NegativeInfinity;
	private int _operationsAtTick;
	private bool _disposed;

	public IncrementalPatternTimeline(SequencingContext root)
	{
		ArgumentNullException.ThrowIfNull(root);
		_root = root;
	}

	public double Tick => _tick;
	public TimeSpan Elapsed { get; private set; }
	public bool IsComplete => _active.Count == 0
		&& _delayed.Count == 0 && _queuedTempoEvents.Count == 0
		&& _futureTempoRamps.Count == 0;

	/// <summary>
	/// True while this invocation has unfinished source rows. Unlike
	/// IsComplete, this does not wait for already-established wall deadlines
	/// belonging to a finished invocation: a Sequence can start its next
	/// order while the previous Pattern's physical note is still pending.
	/// </summary>
	public bool HasUnfinishedRows(long invocationId)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(IncrementalPatternTimeline));
		return _active.Any(c => c.Sequence == invocationId && !c.Complete);
	}

	/// <summary>
	/// True while the invocation has source rows or delayed emissions
	/// outstanding. A completed Pattern can retain a wall-clock note after
	/// its parent Sequence has entered the next order.
	/// </summary>
	public bool HasOutstandingWork(long invocationId)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(IncrementalPatternTimeline));
		return HasUnfinishedRows(invocationId)
			|| _delayed.Any(n => n.Owner.Sequence == invocationId)
			|| _queuedTempoEvents.Any(n => n.InvocationId == invocationId)
			|| _crossTempoSources.Any(n => n.InvocationId == invocationId
				&& _tick < n.EndTick - TickTolerance)
			|| (_futureTempoOwner == invocationId
				&& _futureTempoRamps.Count != 0);
	}

	/// <summary>
	/// Starts a flattened child on this timeline, preserving the shared
	/// musical clock and physical-channel memory while applying the
	/// parent's channel offset and playback multipliers.
	/// </summary>
	public long AddFlattenedChild(
		IIncrementalRawPatternNoteGenerator generator,
		int rowCount,
		SequencingContext parent,
		int physicalChannelOffset = 0,
		int startRow = 0,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(parent);
		if (!ReferenceEquals(_root.State, parent.State)
			|| !ReferenceEquals(_root.ChannelStates, parent.ChannelStates))
			throw new ArgumentException(
				"Parent must belong to the same shared timeline.", nameof(parent));

		SequencingContext child = parent.FlattenedChild(
			pitchMultiplier, playbackSpeedMultiplier, physicalChannelOffset);
		return Add(generator, rowCount, child, startRow);
	}

	/// <summary>
	/// Starts an independent invocation at the current musical instant.
	/// A flattened child may pass a separately mapped SequencingContext,
	/// provided it shares the root's actual clock and channel-state map.
	/// </summary>
	public long Add(
		IIncrementalRawPatternNoteGenerator generator,
		int rowCount,
		SequencingContext context,
		int startRow = 0)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(generator);
		ArgumentNullException.ThrowIfNull(context);
		if (rowCount < 0 || startRow < 0)
			throw new ArgumentOutOfRangeException(
				rowCount < 0 ? nameof(rowCount) : nameof(startRow));
		if (!ReferenceEquals(_root.State, context.State)
			|| !ReferenceEquals(_root.ChannelStates, context.ChannelStates))
			throw new ArgumentException(
				"All merged cursors must share the same sequencing clock and channel memory.",
				nameof(context));
		if (context.FlattenedSourceExpander is not null)
			throw new NotSupportedException(
				"Use explicit cursor invocation, not eager flattened expansion.");

		context.ResolvePatternSourcesAtRowTime = true;
		long id = _nextSequence++;
		_active.Add(new Cursor(generator, context, rowCount,
			startRow, _tick, id));
		return id;
	}

	/// <summary>
	/// Cancels a particular invocation immediately, disposing its suspended
	/// enumerator without affecting other active Patterns. The caller owns
	/// the association between a playback note and its invocation ID.
	/// </summary>
	public bool Cancel(long invocationId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Cursor? cursor = _active.FirstOrDefault(c => c.Sequence == invocationId);
		int removed = _delayed.RemoveAll(note => note.Owner.Sequence == invocationId);
		if (_queuedTempoEvents.Count != 0)
		{
			int size = _queuedTempoEvents.Count;
			for (int i = 0; i < size; i++)
			{
				IncrementalPatternTimelineStep.Emit queued = _queuedTempoEvents.Dequeue();
				if (queued.InvocationId == invocationId)
					removed++;
				else
					_queuedTempoEvents.Enqueue(queued);
			}
		}
		if (_crossTempoSources.RemoveAll(x => x.InvocationId == invocationId) != 0)
		{
			// Recompose only the remaining sources from the *instantaneous*
			// shared Tempo. No future repeat command or cancelled owner's
			// future slide may leak into the surviving Patterns.
			RebuildCrossTempoPlan();
			removed++;
		}
		else if (_futureTempoOwner == invocationId
			&& (_tempoRamp is not null || _futureTempoRamps.Count != 0))
		{
			// A cancelled Pattern no longer owns any future SEy Tempo
			// repetitions. Freeze at the instantaneous shared Tempo;
			// do not let its queued ramps retime unrelated voices.
			_tempoRamp = null;
			_futureTempoRamps.Clear();
			_futureTempoOwner = -1;
			removed++;
		}
		if (cursor is null)
			return removed != 0;
		_active.Remove(cursor);
		cursor.Dispose();
		return true;
	}

	/// <summary>
	/// Advances the globally earliest due cursor operation. Emits at most
	/// one playback event, a musical row Advance, or CPU-only cooperation.
	/// Tempo
	/// integrates only across already-observed tracker ticks; peeking ahead
	/// never commits commands from future rows or fractional positions.
	/// </summary>

	public bool TryStep(out IncrementalPatternTimelineStep? result)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		result = null;
		while (_active.Count != 0 || _delayed.Count != 0
			|| _queuedTempoEvents.Count != 0 || _futureTempoRamps.Count != 0)
		{
			if (TryDequeueTempoEvent(out IncrementalPatternTimelineStep.Emit? queued))
			{
				CheckCooperationBudget();
				result = queued;
				return true;
			}
			Cursor? current = _active
				.OrderBy(c => c.DueTick)
				.ThenBy(c => c.Context.PhysicalChannelBase)
				.ThenBy(c => c.Sequence)
				.FirstOrDefault();
			if (current is not null && current.DueTick < _tick - TickTolerance)
				throw new InvalidOperationException("Incremental cursor moved backwards.");

			if (_crossTempoBoundaryPending)
			{
				// A cursor finishing row N at this tick may immediately
				// start row N+1 with Txx. Cross the row boundary without
				// emitting the old SEy ramp prematurely.
				Cursor? ending = _active
					.Where(c => c.CanFinishRowAt(_tick))
					.OrderBy(c => c.Context.PhysicalChannelBase)
					.ThenBy(c => c.Sequence).FirstOrDefault();
				if (ending is not null)
				{
					if (TryOperate(ending, out result))
						return true;
					continue;
				}
				if (current is null
					|| current.DueTick > _tick + TickTolerance)
				{
					ArbitrateTrackerTempoAtCurrentTick();
					continue;
				}
			}

			TimeSpan nextTickTime = current is null
				? TimeSpan.MaxValue
				: Elapsed + TimeSpan.FromSeconds(
					PredictWallSeconds(current.DueTick));
			DeferredNote? nextWall = _delayed
				.OrderBy(n => n.Deadline)
				.ThenBy(n => n.Owner.Context.PhysicalChannelBase)
				.ThenBy(n => n.Owner.Sequence)
				.ThenBy(n => n.Order)
				.FirstOrDefault();

			// Piecewise Txx ramps have an observable boundary whenever one
			// source's captured row span ends. Activate the next segment
			// before any event/deadline after that tracker tick.
			if (_futureTempoRamps.Count != 0 && _tempoRamp is { } segment)
			{
				double boundaryTick = segment.EndTick;
				TimeSpan boundaryWall = Elapsed + TimeSpan.FromSeconds(
					PredictWallSeconds(boundaryTick));
				if ((current is null
						|| current.DueTick >= boundaryTick - TickTolerance)
					&& (nextWall is null || nextWall.Deadline >= boundaryWall))
				{
					MoveToTick(boundaryTick);
					Elapsed = boundaryWall;
					CheckCooperationBudget();
					if (_crossTempoSources.Count != 0)
					{
						// Do not publish the already-projected next ramp.
						// A new Pattern's row may begin at this *same* tick.
						// Prepare that row first, then arbitrate its Txx
						// together with the SEy boundary instructions.
						_crossTempoBoundaryPending = true;
						continue;
					}
					ActiveTempoRamp nextSegment = _futureTempoRamps.Dequeue();
					// SEy repeats tracker immediate Txx sets at every
					// compatibility-row boundary before applying that
					// repetition's slide. They are *not* applied during
					// preparation of future rows.
					if (nextSegment.BoundaryTempoSets is { } sets)
					{
						foreach (TempoBoundarySet set in sets)
						{
							double tempo = set.Tempo;
							ChannelTarget target = set.Target;
							if (Math.Abs(_root.State.Tempo - tempo) < 1e-12)
								continue;
							_root.State.Tempo = tempo;
							QueueTimingEvent(new NoteEvent(
								new MusicalTime(Elapsed, 0),
								target, [new SetTempoCommand(tempo)]),
								set.InvocationId);
						}
					}
					_tempoRamp = nextSegment;
					if (Math.Abs(nextSegment.StartTempo
						- nextSegment.EndTempo) > 1e-12)
					{
						QueueTimingEvent(new NoteEvent(
							new MusicalTime(Elapsed, 0), ChannelTarget.Global,
							[new SetTempoRampCommand(nextSegment.EndTempo,
								nextSegment.EndTick - nextSegment.StartTick)]),
							_futureTempoOwner);
					}
					continue;
				}
			}

			bool useWall = nextWall is not null
				&& (current is null || nextWall.Deadline < nextTickTime
					|| nextWall.Deadline == nextTickTime
						&& (nextWall.Owner.Context.PhysicalChannelBase
							< current.Context.PhysicalChannelBase
							|| nextWall.Owner.Context.PhysicalChannelBase
								== current.Context.PhysicalChannelBase
								&& nextWall.Owner.Sequence < current.Sequence));
			if (useWall)
			{
				TimeSpan delta = nextWall!.Deadline - Elapsed;
				if (delta < TimeSpan.Zero)
					throw new InvalidOperationException("A delayed raw note moved backwards.");
				MoveByWallSeconds(delta.TotalSeconds);
				Elapsed = nextWall.Deadline;
				CheckCooperationBudget();
				_delayed.Remove(nextWall);
				// SCx/SDx/Qxy synthetic tick results are bounded by the
				// row's actual end, even if SEy extended that row.
				// This check uses the live shared clock, not a wall-time
				// duration guessed when the tracker operation was queued.
				if (nextWall.RowEndTick is double rowEnd
					&& _tick >= rowEnd - TickTolerance)
					continue;

				NoteEvent resolved;
				if (nextWall.IsResolved)
					resolved = nextWall.Raw;
				else
				{
					NoteScheduleBuilder wall = new();
					PatternNoteProcessor.GenerateNotes(
						new SingleEventSlice(nextWall.Raw),
						nextWall.Owner.Context, wall, out _);
					NoteEvent[] generated = wall.Freeze().ToArray();
					if (generated.Length != 1
						|| generated[0].Offset.TimeOffset != TimeSpan.Zero)
						throw new NotSupportedException(
							"Deferred physical note did not resolve to one immediate event.");
					resolved = generated[0];
				}
				NoteEvent note = resolved with
				{
					Offset = new MusicalTime(Elapsed, 0),
					EmissionOrder = _emissionOrder++,
				};
				result = new IncrementalPatternTimelineStep.Emit(note, _tick, Elapsed)
				{
					InvocationId = nextWall.Owner.Sequence,
				};
				return true;
			}

			if (current is null)
				throw new InvalidOperationException("No advancing musical or wall event.");
			MoveToTick(current.DueTick);
			Elapsed = nextTickTime;
			CheckCooperationBudget();
			if (current.Complete)
			{
				_active.Remove(current);
				current.Dispose();
				continue;
			}
			// All invocations whose row begins at this tick must expose
			// their due timing before any one of them is resolved. Otherwise
			// creation order would arbitrarily change simultaneous Txx.
			foreach (Cursor ready in _active
				.Where(c => !c.Complete && !c.InRow
					&& Math.Abs(c.DueTick - _tick) <= TickTolerance)
				.OrderBy(c => c.Context.PhysicalChannelBase)
				.ThenBy(c => c.Sequence).ToArray())
			{
				CheckCooperationBudget();
				if (!ready.BeginRow(_tick, Elapsed))
				{
					result = new IncrementalPatternTimelineStep.Cooperate(
						ready.Sequence, _tick, Elapsed);
					return true;
				}
			}
			// Standalone global Tempo/Speed takes priority over tracker
			// physical-channel Txx effects at a shared row boundary.
			Cursor? globalTiming = _active
				.Where(c => Math.Abs(c.DueTick - _tick) <= TickTolerance
					&& c.DueTiming?.Target.Kind == ChannelTargetKind.Global)
				.OrderBy(c => c.Sequence).FirstOrDefault();
			if (globalTiming is not null)
			{
				if (TryOperate(globalTiming, out result))
					return true;
				continue;
			}
			if (ArbitrateTrackerTempoAtCurrentTick())
			{
				if (TryDequeueTempoEvent(
					out IncrementalPatternTimelineStep.Emit? tempo))
				{
					result = tempo;
					return true;
				}
				continue;
			}
			// Row preparation can change this cursor's next deadline.
			current = _active.OrderBy(c => c.DueTick)
				.ThenBy(c => c.Context.PhysicalChannelBase)
				.ThenBy(c => c.Sequence).First();
			if (current.DueTick > _tick + TickTolerance)
				continue;
			if (TryOperate(current, out result))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Compose simultaneous Txx requests at one row boundary. Their
	/// remembered bytes are resolved in mapped channel order, and on every
	/// subsequent legacy tick the clamps are applied sequentially in that
	/// same order. This matches the common eager processor's Txx arithmetic.
	/// One shared ramp replaces the previous ramp from its *current*
	/// instantaneous Tempo; no event can pre-apply the endpoint.
	/// </summary>
	private bool ArbitrateTrackerTempoAtCurrentTick()
	{
		var pending = _active
			.Where(c => c.InRow && Math.Abs(c.DueTick - _tick) <= TickTolerance)
			.SelectMany(c => c.ReadyTimings
				.Where(t => t.Raw.Commands.Count == 1
					&& t.Raw.Commands[0] is ApplyTrackerTempoCommand)
				.Select(t => (Cursor: c, Timing: t,
					Channel: c.Context.MapPhysicalChannel(t.Raw.Target.PhysicalChannel))))
			.OrderBy(x => x.Timing.EligibleAt)
			.ThenBy(x => x.Channel)
			.ThenBy(x => x.Cursor.Sequence)
			.ThenBy(x => x.Timing.Order)
			.ToArray();
		if (_crossTempoBoundaryPending)
		{
			// At an SEy repeat boundary, incoming Txx is simultaneous
			// with the repeated effects, not an interruption occurring
			// later in musical time.
			ArbitrateCrossTempoBoundary(pending.Select(x => (
				x.Cursor, x.Timing, x.Channel)).ToArray());
			return true;
		}
		if (pending.Length == 0)
			return false;

		// Independent SEy invocations can start a shared Tempo trajectory
		// at this same boundary, while retaining their own captured row
		// spans and future repetition boundaries.
		int repeats = pending.Max(x => x.Cursor.ExtraRowSpans);
		bool crossInvocationRepeat = repeats != 0;
		List<RepeatedTempoSource> crossSources = [];

		// Resolve each original Txx once, in mapped physical order.
		// Capture the resolved bytes; future SEy rows reuse those bytes
		// without repeatedly committing T00 effect memory.
		List<(byte Parameter, ChannelTarget Target)> immediateSets = [];
		List<(byte Parameter, int Span)> slides = [];
		foreach (var request in pending)
		{
			NoteEvent raw = request.Timing.Raw;
			byte input = ((ApplyTrackerTempoCommand)raw.Commands[0]).Parameter;
			byte parameter = request.Cursor.Context
				.GetPhysicalChannelState(raw.Target.PhysicalChannel)
				.ResolveEffectParameter(EffectMemorySlot.Tempo, input);
			if (crossInvocationRepeat && parameter != 0)
				crossSources.Add(new RepeatedTempoSource(
					request.Cursor.Sequence, parameter,
					checked((int)request.Cursor.EffectiveSpanTicks),
					request.Cursor.ExtraRowSpans, request.Channel,
					request.Cursor.Context.MapTarget(raw.Target),
					request.Timing.Order, _tick));
			if (parameter >= 0x20)
			{
				immediateSets.Add((parameter, request.Cursor.Context.MapTarget(raw.Target)));
				_tempoRamp = null;
				_futureTempoRamps.Clear();
				_futureTempoOwner = -1;
				_crossTempoSources.Clear();
				_crossTempoBoundaryPending = false;
				_root.State.Tempo = parameter;
				QueueTimingEvent(raw with
				{
					Target = request.Cursor.Context.MapTarget(raw.Target),
					Commands = [new SetTempoCommand(parameter)],
				}, request.Cursor.Sequence);
			}
			else if (parameter > 0)
			{
				slides.Add((parameter,
					checked((int)request.Cursor.EffectiveSpanTicks)));
			}
			request.Cursor.ConsumeTiming();
		}

		if (crossInvocationRepeat)
		{
			_crossTempoSources.Clear();
			_crossTempoSources.AddRange(crossSources);
			RebuildCrossTempoPlan();
		}

		else if (slides.Count > 0)
		{
			_crossTempoSources.Clear();
			_futureTempoOwner = -1;
			int[] spans = slides.Select(x => x.Span).Distinct()
				.OrderBy(x => x).ToArray();
			double initial = _root.State.Tempo;
			_tempoRamp = null;
			_futureTempoRamps.Clear();
			if (spans.Length == 1)
			{
				// Preserve established same-span IT clamping and output.
				int span = spans[0];
				double ending = initial;
				for (int transition = 1; transition < span; transition++)
					foreach (var slide in slides)
						ending = PatternNoteProcessor.ResolveTrackerTempoAtTick(
							ending, slide.Parameter, firstTick: false);
				if (Math.Abs(ending - initial) > 1e-12)
				{
					SetTempoRampCommand ramp = new(ending, span);
					StartTempoRamp(ramp);
					QueueTimingEvent(new NoteEvent(
						new MusicalTime(Elapsed, 0), ChannelTarget.Global,
						[ramp]), pending[0].Cursor.Sequence);
				}
			}
			else
			{
				// Each slide changes Tempo by (span-1) legacy increments
				// over its own span, exactly as a single-source Txx ramp.
				// Compose those slopes until each source expires. Clamp
				// at the ordered segment endpoints.
				double previousTick = 0;
				double previousTempo = initial;
				List<ActiveTempoRamp> segments = [];
				foreach (int stop in spans)
				{
					double requested = initial;
					foreach (var slide in slides)
					{
						int magnitude = slide.Parameter & 0x0F;
						int sign = slide.Parameter < 0x10 ? -1 : 1;
						requested += sign * magnitude * (slide.Span - 1.0)
							* Math.Min(stop, slide.Span) / slide.Span;
					}
					double ending = Math.Clamp(requested, 32, 255);
					segments.Add(new ActiveTempoRamp(
						_tick + previousTick, _tick + stop,
						previousTempo, ending));
					previousTick = stop;
					previousTempo = ending;
				}
				_tempoRamp = segments[0];
				_futureTempoOwner = pending[0].Cursor.Sequence;
				foreach (ActiveTempoRamp future in segments.Skip(1))
					_futureTempoRamps.Enqueue(future);
				QueueTimingEvent(new NoteEvent(
					new MusicalTime(Elapsed, 0), ChannelTarget.Global,
					[new SetTempoRampCommand(segments[0].EndTempo,
						segments[0].EndTick - segments[0].StartTick)]),
					_futureTempoOwner);
			}
		}
		return true;
	}

	/// <summary>
	/// All Txx operations due at an existing SEy repeat boundary share
	/// the same instant. Apply immediate sets in mapped physical order,
	/// resolve new Txx memory once, and build one combined future slope.
	/// This never enumerates unvisited source rows.
	/// </summary>
	private void ArbitrateCrossTempoBoundary(
		IReadOnlyList<(Cursor Cursor, DeferredTiming Timing, int Channel)> incoming)
	{
		if (!_crossTempoBoundaryPending)
			throw new InvalidOperationException("No pending Tempo boundary.");

		var sets = new List<(
			int Channel, long Owner, long Order, byte Parameter,
			ChannelTarget Target, bool Incoming)>();
		foreach (RepeatedTempoSource source in _crossTempoSources)
		{
			if (source.Parameter >= 0x20 && source.RepeatsAt(_tick))
				sets.Add((source.Channel, source.InvocationId,
					source.SourceOrder, source.Parameter, source.Target, false));
		}

		List<RepeatedTempoSource> newSources = [];
		foreach (var request in incoming)
		{
			NoteEvent raw = request.Timing.Raw;
			byte byteValue = ((ApplyTrackerTempoCommand)raw.Commands[0]).Parameter;
			byte parameter = request.Cursor.Context
				.GetPhysicalChannelState(raw.Target.PhysicalChannel)
				.ResolveEffectParameter(EffectMemorySlot.Tempo, byteValue);
			ChannelTarget mapped = request.Cursor.Context.MapTarget(raw.Target);
			if (parameter != 0)
			{
				newSources.Add(new RepeatedTempoSource(
					request.Cursor.Sequence, parameter,
					checked((int)request.Cursor.EffectiveSpanTicks),
					request.Cursor.ExtraRowSpans, request.Channel,
					mapped, request.Timing.Order, _tick));
				if (parameter >= 0x20)
					sets.Add((request.Channel, request.Cursor.Sequence,
						request.Timing.Order, parameter, mapped, true));
			}
			request.Cursor.ConsumeTiming();
		}

		foreach (var set in sets.OrderBy(x => x.Channel)
			.ThenBy(x => x.Owner).ThenBy(x => x.Order))
		{
			// An original Txx emits its immediate setting even when it
			// matches the existing Tempo; repeated settings do not.
			if (!set.Incoming && Math.Abs(_root.State.Tempo - set.Parameter) <= 1e-12)
				continue;
			_root.State.Tempo = set.Parameter;
			QueueTimingEvent(new NoteEvent(
				new MusicalTime(Elapsed, 0), set.Target,
				[new SetTempoCommand(set.Parameter)]), set.Owner);
		}

		_crossTempoSources.RemoveAll(x => _tick >= x.EndTick - TickTolerance);
		_crossTempoSources.AddRange(newSources);
		_crossTempoBoundaryPending = false;
		RebuildCrossTempoPlan();
	}

	private void RebuildCrossTempoPlan()
	{
		// Rebuilds are also used after cancelling an individual contributor.
		// The first segment always begins at the *current* shared Tempo.
		// Only finite, bounded SEy metadata is inspected: no Pattern
		// enumerator or future row is consumed.
		_tempoRamp = null;
		_futureTempoRamps.Clear();
		_futureTempoOwner = -1;
		RepeatedTempoSource[] active = _crossTempoSources
			.Where(x => _tick < x.EndTick - TickTolerance)
			.OrderBy(x => x.Channel)
			.ThenBy(x => x.InvocationId)
			.ThenBy(x => x.SourceOrder)
			.ToArray();
		if (active.Length == 0)
			return;

		_futureTempoOwner = active[0].InvocationId;
		double[] boundaries = active
			.SelectMany(x => Enumerable.Range(1, x.Repeats + 1)
				.Select(k => x.OriginTick + (double)x.Span * k))
			.Where(t => t > _tick + TickTolerance)
			.Distinct()
			.OrderBy(t => t)
			.ToArray();
		double startingTick = _tick;
		double startingTempo = _root.State.Tempo;
		List<ActiveTempoRamp> segments = [];
		for (int i = 0; i < boundaries.Length; i++)
		{
			// A repeated T20–TFF set is re-applied at that source's own
			// compatibility-row boundary, not when this plan is built.
			List<TempoBoundarySet> resets = [];
			if (i > 0)
			{
				foreach (RepeatedTempoSource source in active)
				{
					if (source.Parameter < 0x20)
						continue;
					if (!source.RepeatsAt(startingTick))
						continue;
					resets.Add(new TempoBoundarySet(
						source.Parameter, source.Target, source.InvocationId));
				}
			}
			if (resets.Count != 0)
				startingTempo = resets[^1].Tempo;

			double endTick = boundaries[i];
			double slope = 0;
			foreach (RepeatedTempoSource source in active)
			{
				if (source.Parameter == 0 || source.Parameter >= 0x20
					|| startingTick >= source.EndTick
						- TickTolerance)
					continue;
				int sign = source.Parameter < 0x10 ? -1 : 1;
				slope += sign * (source.Parameter & 0x0F)
					* (source.Span - 1.0) / source.Span;
			}
			double endingTempo = Math.Clamp(
				startingTempo + slope * (endTick - startingTick), 32, 255);
			segments.Add(new ActiveTempoRamp(
				startingTick, endTick, startingTempo, endingTempo,
				resets.Count == 0 ? null : resets));
			startingTick = endTick;
			startingTempo = endingTempo;
		}
		if (segments.Count == 0)
			return;
		_tempoRamp = segments[0];
		foreach (ActiveTempoRamp following in segments.Skip(1))
			_futureTempoRamps.Enqueue(following);
		if (Math.Abs(segments[0].EndTempo
			- segments[0].StartTempo) > 1e-12)
		{
			QueueTimingEvent(new NoteEvent(
				new MusicalTime(Elapsed, 0), ChannelTarget.Global,
				[new SetTempoRampCommand(
					segments[0].EndTempo,
					segments[0].EndTick - segments[0].StartTick)]),
				_futureTempoOwner);
		}
	}

	// Match the eager processor's equal-wall-time note ordering:
	// global commands precede physical channels at a common boundary,
	// with stable order within the same target. Keep this ordering on
	// *emission*, not on resolution of state/memory for Txx.
	private bool TryDequeueTempoEvent(
		out IncrementalPatternTimelineStep.Emit? result)
	{
		if (_queuedTempoEvents.Count == 0)
		{
			result = null;
			return false;
		}
		IncrementalPatternTimelineStep.Emit[] ordered = _queuedTempoEvents
			.Select((step, index) => (step, index))
			.OrderBy(x => x.step.Note.Offset.TimeOffset)
			.ThenBy(x => x.step.Note.Target.Kind)
			.ThenBy(x => x.step.Note.Target.PhysicalChannel)
			.ThenBy(x => x.index)
			.Select(x => x.step).ToArray();
		_queuedTempoEvents.Clear();
		foreach (IncrementalPatternTimelineStep.Emit item in ordered.Skip(1))
			_queuedTempoEvents.Enqueue(item);
		result = ordered[0];
		return true;
	}

	private void QueueTimingEvent(NoteEvent note, long invocationId)
	{
		NoteEvent resolved = note with
		{
			Offset = new MusicalTime(Elapsed, 0),
			EmissionOrder = _emissionOrder++,
		};
		_queuedTempoEvents.Enqueue(
			new IncrementalPatternTimelineStep.Emit(resolved, _tick, Elapsed)
			{
				InvocationId = invocationId,
			});
	}

	// All cursor deadlines remain in musical ticks. The shared ramp is
	// consulted only while moving the real clock to the next due action;
	// looking ahead must not apply its future Tempo to SequencingState.
	private double PredictWallSeconds(double targetTick)
	{
		double remaining = Math.Max(0, targetTick - _tick);
		if (_tempoRamp is null || remaining == 0)
			return remaining * SequencingConstants.Diachron.TotalSeconds
				/ _root.State.Tempo;
		double rampTicks = Math.Min(remaining, _tempoRamp.EndTick - _tick);
		if (rampTicks <= 0)
			return remaining * SequencingConstants.Diachron.TotalSeconds
				/ _root.State.Tempo;
		TrackerTimeMap integral = new(_root.State.Tempo);
		integral.AppendTempoRamp(_tempoRamp.TempoAt(_tick + rampTicks), rampTicks);
		return integral.CurrentTimeSeconds
			+ (remaining - rampTicks) * SequencingConstants.Diachron.TotalSeconds
				/ _tempoRamp.EndTempo;
	}

	private void MoveToTick(double targetTick)
	{
		if (targetTick < _tick - TickTolerance)
			throw new InvalidOperationException("The shared tick clock moved backwards.");
		if (_tempoRamp is not null && targetTick >= _tempoRamp.EndTick - TickTolerance)
		{
			_root.State.Tempo = _tempoRamp.EndTempo;
			_tempoRamp = null;
		}
		else if (_tempoRamp is not null)
		{
			_root.State.Tempo = _tempoRamp.TempoAt(targetTick);
		}
		_tick = targetTick;
	}

	// A positive fixed wall deadline can fall *inside* a ramp. Invert the
	// same TrackerTimeMap integral used by the established eager processor.
	private void MoveByWallSeconds(double seconds)
	{
		if (seconds < 0)
			throw new InvalidOperationException("The shared wall clock moved backwards.");
		if (seconds == 0)
			return;
		if (_tempoRamp is { } ramp)
		{
			double ticksLeft = ramp.EndTick - _tick;
			if (ticksLeft > TickTolerance)
			{
				TrackerTimeMap map = new(_root.State.Tempo);
				map.AppendTempoRamp(ramp.EndTempo, ticksLeft);
				if (seconds < map.CurrentTimeSeconds)
				{
					double localTicks = map.GetTickAtTime(seconds);
					_tick += localTicks;
					_root.State.Tempo = map.GetTempoAtTick(localTicks);
					return;
				}
				seconds -= map.CurrentTimeSeconds;
				_tick = ramp.EndTick;
				_root.State.Tempo = ramp.EndTempo;
			}
			_tempoRamp = null;
		}
		_tick += seconds * _root.State.Tempo
			/ SequencingConstants.Diachron.TotalSeconds;
	}

	private void StartTempoRamp(SetTempoRampCommand ramp)
	{
		// A later row-boundary Txx replaces the prior trajectory from the
		// instantaneous Tempo reached at this tracker tick.
		_futureTempoRamps.Clear();
		_futureTempoOwner = -1;
		_crossTempoSources.Clear();
		_crossTempoBoundaryPending = false;
		_tempoRamp = new ActiveTempoRamp(
			_tick, _tick + ramp.TrackerTicks, _root.State.Tempo, ramp.EndingTempo);
	}

	private void CheckCooperationBudget()
	{
		if (Math.Abs(_tick - _lastStepTick) > TickTolerance)
		{
			_lastStepTick = _tick;
			_operationsAtTick = 0;
		}
		if (++_operationsAtTick > MaximumOperationsAtOneTick)
			throw new InvalidOperationException(
					"Incremental scheduler exceeded its same-tick cooperation budget.");
	}


	private bool TryOperate(Cursor current, out IncrementalPatternTimelineStep? result)
	{
		result = null;
		if (!current.InRow)
		{
			if (!current.BeginRow(_tick, Elapsed))
			{
				result = new IncrementalPatternTimelineStep.Cooperate(
					current.Sequence, _tick, Elapsed);
				return true;
			}
			return false;
		}
		if (current.DueTiming is { } timing)
		{
			// Eligible timing executes only at the invocation's row boundary.
			// The common PatternNoteProcessor owns Txx memory, clamping, and
			// its conversion to a continuous SetTempoRampCommand.
			bool trackerTempo = timing.Commands.Count == 1
				&& timing.Commands[0] is ApplyTrackerTempoCommand;
			if (trackerTempo)
				throw new InvalidOperationException(
					"Tracker Txx must be resolved through same-boundary arbitration.");
			if (timing.Commands.Any(c => c is SetTempoCommand))
			{
				// A new direct Tempo command truncates any previous ramp now.
				_tempoRamp = null;
				_futureTempoRamps.Clear();
				_futureTempoOwner = -1;
				_crossTempoSources.Clear();
				_crossTempoBoundaryPending = false;
			}

			NoteScheduleBuilder resolvedTiming = new();
			if (trackerTempo)
			{
				// The eager processor resolves a *whole* row and thus leaves
				// SequencingState at the ramp's endpoint. Restore it: this
				// shared clock must evolve only as actual time advances.
				double initialTempo = current.Context.State.Tempo;
				int initialSpeed = current.Context.State.Speed;
				try
				{
					current.Context.State.Speed = checked((int)current.RowSpeed);
					PatternNoteProcessor.GenerateNotes(
						new SingleEventSlice(timing with { Offset = MusicalTime.Zero }),
						current.Context, resolvedTiming, out _);
				}
				finally
				{
					current.Context.State.Tempo = initialTempo;
					current.Context.State.Speed = initialSpeed;
				}
			}
			else
			{
				PatternNoteProcessor.GenerateNotes(
					new SingleEventSlice(timing with { Offset = MusicalTime.Zero }),
					current.Context, resolvedTiming, out _);
			}
			current.ConsumeTiming();
			NoteEvent[] due = resolvedTiming.Freeze().ToArray();
			if (due.Length > 1
				|| due.Any(e => e.Offset.TimeOffset != TimeSpan.Zero))
				throw new NotSupportedException(
					"Incremental tracker timing produced unsupported extra operations.");
			if (due.Length == 0)
				return false;

			if (due[0].Commands.Count == 1
				&& due[0].Commands[0] is SetTempoRampCommand ramp)
				StartTempoRamp(ramp);
			else if (trackerTempo
				&& due[0].Commands.Count == 1
				&& due[0].Commands[0] is SetTempoCommand set)
				_root.State.Tempo = set.TicksPerDiachron;

			NoteEvent emittedTiming = due[0] with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(
				emittedTiming, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		if (current.DueEvent is { } raw)
		{
			byte? cutTick = null;
			byte? delayTick = null;
			byte? retrigger = null;
			List<NoteCommand> ordinary = [];
			foreach (NoteCommand command in raw.Commands)
			{
				switch (command)
				{
					case ApplyTrackerNoteCutCommand cut:
						cutTick = cut.Tick;
						break;
					case ApplyTrackerNoteDelayCommand delay:
						delayTick = delay.Tick;
						break;
					case ApplyRetriggerCommand q:
						retrigger = q.Parameter;
						break;
					default:
						ordinary.Add(command);
						break;
				}
			}
			// SDx shifts the note's actual execution position; Qxy
			// evaluates future retrigger ticks from that delayed position.
			if (cutTick.HasValue || delayTick.HasValue || retrigger.HasValue)
			{
				// A note delayed by SDx is resolved only when its tick is
				// reached; this preserves atomic note setup and Source memory.
				NoteEvent note = raw with { Commands = ordinary.ToArray() };
				if (delayTick.HasValue && ordinary.Count > 0)
					current.QueueDelayed(note, delayTick.Value);
				if (cutTick.HasValue)
					current.QueueCut(raw, cutTick.Value);
				if (retrigger.HasValue)
				{
					// Eager Qxy commits its parameter byte even if SDx's
					// note can never start before this row ends; in that
					// case it does not initialize or advance countdown.
					int shift = delayTick.HasValue
						? Math.Max(1, delayTick.Value) : 0;
					double startTick = _tick + shift;
					bool startCanExecute =
						!delayTick.HasValue || shift < current.EffectiveSpanTicks;
					if (startCanExecute && raw.Offset.TimeOffset > TimeSpan.Zero)
					{
						double firstWall = PredictWallSeconds(startTick)
							+ raw.Offset.TimeOffset.TotalSeconds;
						double endWall = PredictWallSeconds(current.RowEndTick);
						startCanExecute = firstWall < endWall - 1e-9;
					}
					current.QueueRetrigger(raw, retrigger.Value, delayTick,
						startCanExecute);
				}
				if (delayTick.HasValue || ordinary.Count == 0)
				{
					current.ConsumeEvent();
					return false;
				}
				raw = note;
			}
			// Fixed wall-time begins when this musical position is reached.
			// Resolve commands only upon reaching the established deadline,
			// even if another cursor changes tempo in between.
			if (raw.Offset.TimeOffset > TimeSpan.Zero)
			{
				_delayed.Add(new DeferredNote(current,
					raw with { Offset = MusicalTime.Zero },
					Elapsed + raw.Offset.TimeOffset, _nextDeferredOrder++));
				current.ConsumeEvent();
				return false;
			}

			NoteScheduleBuilder resolved = new();
			PatternNoteProcessor.GenerateNotes(
				new SingleEventSlice(raw), current.Context, resolved,
				out TimeSpan nominalDuration);
			current.ConsumeEvent();
			List<NoteEvent> immediate = [];
			foreach (NoteEvent generated in resolved.Freeze())
			{
				// S6x changes the renderer's continuous-effect tick span.
				// Use the very same override table as the eager processor,
				// without processing future rows or replaying commands.
				NoteEvent note = current.FineDelayTicks == 0 ? generated
					: generated with
					{
						Commands = generated.Commands.Select(c =>
							PatternNoteProcessor.ApplyRowTickOverride(
								c, checked((int)current.EffectiveSpanTicks))!)
							.ToArray(),
					};
				// The common PatternNoteProcessor is authoritative for slide
				// transformations and tracker-effect memory. Its generated
				// row-end freezes are deferred until this cursor's own row end.
				if (note.Offset.TimeOffset == nominalDuration
					&& note.Commands.Count != 0
					&& note.Commands.All(c => c is ClearPitchSlideCommand
						or ClearNoteVolumeSlideCommand or ClearOverallChannelVolumeSlideCommand
						or ClearGlobalVolumeSlideCommand or ClearSpatialXSlideCommand))
				{
					current.QueueCleanup(note);
					continue;
				}
				if (note.Offset.TimeOffset != TimeSpan.Zero)
					throw new NotSupportedException(
						"Incremental raw event resolved into an unsupported delayed operation.");
				immediate.Add(note);
			}
			if (immediate.Count > 1)
				throw new NotSupportedException(
					"One raw cursor event resolved into multiple immediate operations.");
			if (immediate.Count == 0)
				return false;
			if (current.ExtraRowSpans > 0)
				current.QueueRepeats(immediate[0],
					(raw.Offset.RowOffset - current.Row) * current.RowSpeed);
			NoteEvent emitted = immediate[0] with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		if (current.DueScheduled is { } scheduled)
		{
			current.ConsumeScheduled();
			NoteEvent? output = null;
			if (scheduled.Kind == TickOperationKind.Retrigger)
			{
				NoteEvent? retrigger = current.ExecuteRetriggerTick(scheduled);
				if (retrigger is not null)
					output = retrigger with
					{
						Target = current.Context.MapTarget(retrigger.Target),
					};
			}
			else if (scheduled.Kind == TickOperationKind.Cut)
				output = scheduled.Note with
				{
					Target = current.Context.MapTarget(scheduled.Note.Target),
				};
			else if (scheduled.Kind == TickOperationKind.RepeatedDelayed)
				// The first SDx execution already mapped this target.
				output = scheduled.Note;
			else
			{
				// SDx resolves and commits command memory once, at its first
				// actual delayed tick. Later SEy copies reuse those resolved
				// commands, without replaying tracker command memory.
				NoteScheduleBuilder builder = new();
				PatternNoteProcessor.GenerateNotes(
					new SingleEventSlice(scheduled.Note), current.Context, builder,
					out TimeSpan nominal);
				List<NoteEvent> resolved = [];
				foreach (NoteEvent note in builder.Freeze())
				{
					if (note.Offset.TimeOffset == nominal
						&& note.Commands.Count > 0
						&& note.Commands.All(c => c is ClearPitchSlideCommand
							or ClearNoteVolumeSlideCommand
							or ClearOverallChannelVolumeSlideCommand
							or ClearGlobalVolumeSlideCommand or ClearSpatialXSlideCommand))
					{
						current.QueueCleanup(note);
						continue;
					}
					if (note.Offset.TimeOffset != TimeSpan.Zero)
						throw new NotSupportedException(
							"SDx resolved an unsupported deferred effect.");
					resolved.Add(note);
				}
				if (resolved.Count > 1)
					throw new NotSupportedException(
						"SDx produced multiple independent note operations.");
				if (resolved.Count == 1)
				{
					// Preserve the cell's independent wall offset for
					// every SEy note copy. Its Source/volume memory has
					// already been committed exactly once here.
					output = resolved[0] with
					{
						Offset = new MusicalTime(
							scheduled.Note.Offset.TimeOffset, 0),
					};
					current.QueueDelayedCopies(scheduled, output);
				}
			}
			if (output is null)
				return false;
			if (output.Offset.TimeOffset > TimeSpan.Zero)
			{
				// A tracker tick remains a musical deadline; the fixed
				// TimeOffset delays only its resulting note operation.
				// The ordinary positive-offset path stays independently
				// unresolved until delivery (above).
				_delayed.Add(new DeferredNote(
					current,
					output with { Offset = MusicalTime.Zero },
					Elapsed + output.Offset.TimeOffset,
					_nextDeferredOrder++, IsResolved: true,
					RowEndTick: current.RowEndTick));
				return false;
			}
			NoteEvent emitted = output with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		if (current.DueRepeated is { } repeating)
		{
			current.ConsumeRepeated();
			NoteEvent emitted = repeating with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		// An SBx backward visit reorders a tick-zero start from an
		// earlier source row ahead of the previous row's ending clear.
		current.DeferCleanupForBackwardLoop();
		if (current.DueWrapCleanup is { } wrapped)
		{
			current.ConsumeWrapCleanup();
			NoteEvent emitted = wrapped with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		if (current.DueCleanup is { } cleanup)
		{
			current.ConsumeCleanup();
			NoteEvent emitted = cleanup with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed)
			{
				InvocationId = current.Sequence,
			};
			return true;
		}
		// Tracker tick effects with a fixed wall offset may remain in
		// the wall queue after their containing row has ended. They
		// must not emit outside that row (ordinary wall-offset notes
		// have no such restriction).
		_delayed.RemoveAll(n => ReferenceEquals(n.Owner, current)
			&& n.RowEndTick is double limit
			&& _tick >= limit - TickTolerance);
		PatternFlowControl completed = current.FinishRow();
		result = completed.HasControl
			? new IncrementalPatternTimelineStep.Flow(
				current.Sequence, completed, _tick, Elapsed)
			: new IncrementalPatternTimelineStep.Advance(_tick, Elapsed);
		return true;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		foreach (Cursor cursor in _active)
			cursor.Dispose();
		_active.Clear();
		_delayed.Clear();
		_queuedTempoEvents.Clear();
		_futureTempoRamps.Clear();
		_crossTempoSources.Clear();
		_crossTempoBoundaryPending = false;
	}
}
