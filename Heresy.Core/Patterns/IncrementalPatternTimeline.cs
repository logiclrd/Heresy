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
		: IncrementalPatternTimelineStep(Tick, Time);

	public sealed record Advance(double Tick, TimeSpan Time)
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
		private readonly IEnumerator<RawPatternStep> _source;
		private readonly List<DeferredTiming> _pendingTiming = [];
		private readonly List<DeferredTiming> _readyTiming = [];
		private long _nextTimingOrder;
		private RawPatternStep? _lookahead;
		private bool _ended;
		private double _lastRow;
		private IReadOnlyList<NoteEvent> _rowEvents = [];
		private readonly List<NoteEvent> _rowEndCommands = [];
		private readonly List<(double TickOffset, NoteEvent Note)> _repeated = [];
		private readonly List<TickOperation> _scheduled = [];
		private long _scheduledOrder;
		private int _fineDelayTicks;
		private int _extraRowSpans;
		private int _eventIndex;
		private bool _inRow;
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
			Row = Math.Min(rowCount, startRow);
			DueTick = tick;
			Sequence = sequence;
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
		public NoteEvent? DueRepeated => _inRow && _repeated.Count != 0
			&& (_eventIndex >= _rowEvents.Count
				|| _repeated[0].TickOffset < RawEventTickOffset - TickTolerance)
			&& (_scheduled.Count == 0
				|| _repeated[0].TickOffset <= _scheduled[0].Offset + TickTolerance)
			? _repeated[0].Note : null;
		public TickOperation? DueScheduled => _inRow && _scheduled.Count != 0
			&& (_eventIndex >= _rowEvents.Count
				|| _scheduled[0].Offset < RawEventTickOffset - TickTolerance)
			&& (_repeated.Count == 0
				|| _scheduled[0].Offset < _repeated[0].TickOffset - TickTolerance)
			? _scheduled[0] : null;

		private void Schedule(double offset, NoteEvent note,
			TickOperationKind kind, int interval = 0, byte volumeTransform = 0)
		{
			_scheduled.Add(new TickOperation(offset, note, kind,
				_scheduledOrder++, interval, volumeTransform));
			_scheduled.Sort((a, b) =>
			{
				int order = a.Offset.CompareTo(b.Offset);
				return order != 0 ? order : a.Order.CompareTo(b.Order);
			});
			RefreshDue();
		}

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

		public void QueueRetrigger(NoteEvent note, byte input)
		{
			SequencingChannelState channel =
				Context.GetPhysicalChannelState(note.Target.PhysicalChannel);
			byte parameter = channel.ResolveEffectParameter(
				EffectMemorySlot.Retrigger, input);
			int interval = parameter & 0x0F;
			byte transform = (byte)(parameter >> 4);
			bool startsNew = note.Commands.Any(c => c is StartNoteCommand);
			if (startsNew)
				channel.RetriggerCountdown = interval;
			double original = (note.Offset.RowOffset - Row) * RowSpeed;
			for (int tick = startsNew ? 1 : 0; original + tick < TotalRowTicks; tick++)
			{
				Schedule(original + tick, note, TickOperationKind.Retrigger,
					interval, transform);
			}
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
				? _rowEvents[_eventIndex] : null;
		public NoteEvent? DueTiming =>
			_inRow && _readyTiming.Count > 0
				? _readyTiming[0].Raw : null;
		public IReadOnlyList<DeferredTiming> ReadyTimings => _readyTiming;
		public NoteEvent? DueCleanup =>
			_inRow && DueEvent is null && DueRepeated is null && DueScheduled is null
			&& _rowEndCommands.Count > 0
				? _rowEndCommands[0] : null;

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

		public void BeginRow(double tick, TimeSpan now)
		{
			if (_inRow || Complete)
				throw new InvalidOperationException("Cursor row is not available.");

			_rowStartTick = tick;
			_rowSpeed = Context.State.Speed;
			_fineDelayTicks = 0;
			_extraRowSpans = 0;
			_repeated.Clear();
			_scheduled.Clear();
			List<NoteEvent> events = [];
			List<(int Channel, int Order, byte ExtraRows)> rowDelays = [];
			int sourceOrder = 0;
			int inspected = 0;
			while (TryPeek(out RawPatternStep? step))
			{
				if (++inspected > MaximumRawStepsPerRow)
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
						else if (command is ApplyTrackerPatternDelayCommand delay)
						{
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new InvalidOperationException("SEy requires a physical channel.");
							if (emit.Note.Offset.TimeOffset != TimeSpan.Zero)
								throw new NotSupportedException("Delayed SEy scheduling is not supported.");
							rowDelays.Add((Context.MapPhysicalChannel(
								emit.Note.Target.PhysicalChannel), sourceOrder, delay.ExtraRows));
						}
						else
							commands.Add(command);
					}
					sourceOrder++;
					if (commands.Count == 0)
						continue;
					NoteEvent filtered = emit.Note with { Commands = commands.ToArray() };
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
						events.Add(filtered);
					}
				}
			}

			if (rowDelays.Count != 0)
				_extraRowSpans = rowDelays.OrderBy(x => x.Channel)
					.ThenBy(x => x.Order).First().ExtraRows;
			if (_extraRowSpans != 0 && _pendingTiming.Any(t =>
				t.Raw.Commands.Any(c => c is ApplyTrackerTempoCommand)))
				throw new NotSupportedException(
					"SEy with tracker Txx requires repeated-span tempo arbitration.");
			// Stable equal-time order: timing at the beginning of the row
			// (including fractional Timing commands) comes before notes;
			// then physical channels, finally the producer's emission order.
			_rowEvents = events.Select((note, index) => (note, index))
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
			RefreshDue();
		}

		private bool TryPeek(out RawPatternStep? result)
		{
			if (_lookahead is null && !_ended)
			{
				if (_source.MoveNext())
				{
					RawPatternStep step = _source.Current
						?? throw new InvalidOperationException("Raw Pattern emitted null.");
					if (!double.IsFinite(step.Row) || step.Row < _lastRow
						|| step.Row < 0)
						throw new InvalidOperationException(
							"Raw Pattern positions must be nonnegative and nondecreasing.");
					_lastRow = step.Row;
					_lookahead = step;
				}
				else
				{
					_ended = true;
				}
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

		public void FinishRow()
		{
			if (!_inRow || DueEvent is not null)
				throw new InvalidOperationException("Cursor row is not finished.");
			_inRow = false;
			_rowEvents = [];
			_rowEndCommands.Clear();
			_repeated.Clear();
			_scheduled.Clear();
			_readyTiming.Clear();
			Row++;
			// The next row is eligible at exactly this tick. The caller
			// decides when it executes relative to other due cursors.
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
			DueTick = _rowStartTick + Math.Min(rawTick, Math.Min(repeatTick, commandTick));
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
			if (note.Commands.Any(c => c is ApplyTrackerTempoCommand)
				&& !IsTiming(note))
				throw new NotSupportedException(
					"Tracker Txx in a mixed-command raw note requires resumable row resolution.");
			if (note.Offset.TimeOffset < TimeSpan.Zero)
				throw new NotSupportedException(
					"The incremental tick merger does not support negative wall-time offsets.");
			if (note.Offset.TimeOffset > TimeSpan.Zero
				&& !IsTiming(note)
				&& (note.Target.Kind != ChannelTargetKind.Physical
					|| note.Commands.Any(c => c is not (StartNoteCommand
						or NoteOffCommand or NoteCutCommand))))
				throw new NotSupportedException(
					"Positive fixed wall-time offsets require standalone global Tempo/Speed or ordinary physical Note/Off/Cut.");
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
		Cursor Owner, NoteEvent Raw, TimeSpan Deadline, long Order);

	/// <summary>
	/// A tracker Tempo ramp is linear in musical tick position, never wall
	/// time. Its starting Tempo is the *actual* shared Tempo at its boundary.
	/// </summary>
	private sealed record ActiveTempoRamp(
		double StartTick, double EndTick,
		double StartTempo, double EndTempo)
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
		&& _delayed.Count == 0 && _queuedTempoEvents.Count == 0;

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
		if (cursor is null)
			return removed != 0;
		_active.Remove(cursor);
		cursor.Dispose();
		return true;
	}

	/// <summary>
	/// Advances the globally earliest due cursor operation. Emits at most
	/// one playback event or a silent row-boundary cooperation step. Tempo
	/// integrates only across already-observed tracker ticks; peeking ahead
	/// never commits commands from future rows or fractional positions.
	/// </summary>

	public bool TryStep(out IncrementalPatternTimelineStep? result)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		result = null;
		while (_active.Count != 0 || _delayed.Count != 0 || _queuedTempoEvents.Count != 0)
		{
			if (_queuedTempoEvents.TryDequeue(out IncrementalPatternTimelineStep.Emit? queued))
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

				NoteScheduleBuilder wall = new();
				PatternNoteProcessor.GenerateNotes(
					new SingleEventSlice(nextWall.Raw), nextWall.Owner.Context,
					wall, out _);
				NoteEvent[] resolvedWall = wall.Freeze().ToArray();
				if (resolvedWall.Length != 1
					|| resolvedWall[0].Offset.TimeOffset != TimeSpan.Zero)
					throw new NotSupportedException(
						"Deferred physical note did not resolve to one immediate event.");
				NoteEvent note = resolvedWall[0] with
				{
					Offset = new MusicalTime(Elapsed, 0),
					EmissionOrder = _emissionOrder++,
				};
				result = new IncrementalPatternTimelineStep.Emit(note, _tick, Elapsed);
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
				ready.BeginRow(_tick, Elapsed);
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
				if (_queuedTempoEvents.TryDequeue(
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
		if (pending.Length == 0)
			return false;

		// A ramp from several sources has one duration only when they
		// agree on the tick span. Defer mixed-speed arbitration rather
		// than quietly using whichever invocation happened to run first.
		if (pending.Any(x => x.Cursor.ExtraRowSpans != 0))
			throw new NotSupportedException(
				"SEy repeating Txx ramps requires a resumable multi-span tempo state machine.");
		int[] slideSpans = pending
			.Select(x => checked((int)x.Cursor.EffectiveSpanTicks))
			.Distinct().ToArray();
		if (slideSpans.Length > 1)
			throw new NotSupportedException(
				"Simultaneous Txx slides with different captured row speeds require separate arbitration.");

		List<byte> slides = [];
		foreach (var request in pending)
		{
			NoteEvent raw = request.Timing.Raw;
			byte input = ((ApplyTrackerTempoCommand)raw.Commands[0]).Parameter;
			byte parameter = request.Cursor.Context
				.GetPhysicalChannelState(raw.Target.PhysicalChannel)
				.ResolveEffectParameter(EffectMemorySlot.Tempo, input);
			if (parameter >= 0x20)
			{
				// A new Tempo set cuts off any previous ramp at the
				// current shared tick, never at its future endpoint.
				_tempoRamp = null;
				_root.State.Tempo = parameter;
				QueueTimingEvent(raw with
				{
					Target = request.Cursor.Context.MapTarget(raw.Target),
					Commands = [new SetTempoCommand(parameter)],
				});
			}
			else if (parameter > 0)
			{
				slides.Add(parameter);
			}
			request.Cursor.ConsumeTiming();
		}

		if (slides.Count > 0)
		{
			int span = slideSpans[0];
			double initial = _root.State.Tempo;
			double ending = initial;
			for (int transition = 1; transition < span; transition++)
			{
				foreach (byte parameter in slides)
				{
					ending = PatternNoteProcessor.ResolveTrackerTempoAtTick(
						ending, parameter, firstTick: false);
				}
			}
			_tempoRamp = null;
			if (Math.Abs(ending - initial) > 1e-12)
			{
				SetTempoRampCommand ramp = new(ending, span);
				StartTempoRamp(ramp);
				QueueTimingEvent(new NoteEvent(
					new MusicalTime(Elapsed, 0), ChannelTarget.Global,
					[ramp]));
			}
		}
		return true;
	}

	private void QueueTimingEvent(NoteEvent note)
	{
		NoteEvent resolved = note with
		{
			Offset = new MusicalTime(Elapsed, 0),
			EmissionOrder = _emissionOrder++,
		};
		_queuedTempoEvents.Enqueue(
			new IncrementalPatternTimelineStep.Emit(resolved, _tick, Elapsed));
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
			current.BeginRow(_tick, Elapsed);
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
				emittedTiming, _tick, Elapsed);
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
			if (delayTick.HasValue && retrigger.HasValue)
				throw new NotSupportedException(
					"Combined SDx/Qxy needs a shared retrigger/delay state machine.");
			if ((cutTick.HasValue || delayTick.HasValue || retrigger.HasValue)
				&& raw.Offset.TimeOffset != TimeSpan.Zero)
				throw new NotSupportedException(
					"SCx/SDx/Qxy with fixed wall offsets is not yet supported.");
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
					current.QueueRetrigger(raw, retrigger.Value);
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
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed);
			return true;
		}
		if (current.DueScheduled is { } scheduled)
		{
			current.ConsumeScheduled();
			NoteEvent? output = null;
			if (scheduled.Kind == TickOperationKind.Retrigger)
				output = current.ExecuteRetriggerTick(scheduled);
			else if (scheduled.Kind is TickOperationKind.Cut
				or TickOperationKind.RepeatedDelayed)
				output = scheduled.Note with
				{
					Target = current.Context.MapTarget(scheduled.Note.Target),
				};
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
					output = resolved[0];
					current.QueueDelayedCopies(scheduled, output);
				}
			}
			if (output is null)
				return false;
			NoteEvent emitted = output with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed);
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
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed);
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
			result = new IncrementalPatternTimelineStep.Emit(emitted, _tick, Elapsed);
			return true;
		}
		current.FinishRow();
		result = new IncrementalPatternTimelineStep.Advance(_tick, Elapsed);
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
	}
}
