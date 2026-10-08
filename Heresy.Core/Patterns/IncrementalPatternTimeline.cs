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
		public NoteEvent? DueEvent =>
			_inRow && _eventIndex < _rowEvents.Count
				? _rowEvents[_eventIndex] : null;
		public NoteEvent? DueTiming =>
			_inRow && _readyTiming.Count > 0
				? _readyTiming[0].Raw : null;
		public NoteEvent? DueCleanup =>
			_inRow && DueEvent is null && _rowEndCommands.Count > 0
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
			List<NoteEvent> events = [];
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
					Validate(emit.Note);
					if (IsTiming(emit.Note))
					{
						// Commands exactly at the Pattern endpoint have no
						// eligible source row. Do not apply them to the
						// preceding final row.
						if (emit.Note.Offset.RowOffset >= RowCount)
							continue;
						// Fractional timing positions apply at the start of
						// their nominal row. A positive fixed offset is an
						// eligibility deadline, never an execution timestamp.
						_pendingTiming.Add(new DeferredTiming(
							emit.Note, now + emit.Note.Offset.TimeOffset,
							_nextTimingOrder++));
					}
					else
					{
						events.Add(emit.Note);
					}
				}
			}

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
			double position = DueEvent is { } due
				? DuePosition(due, Row, RowCount) : Row + 1.0;
			DueTick = _rowStartTick + (position - Row) * _rowSpeed;
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
						or ApplyTrackerTempoCommand;
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
	public bool IsComplete => _active.Count == 0 && _delayed.Count == 0;

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
		while (_active.Count != 0 || _delayed.Count != 0)
		{
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
					Math.Max(0, current.DueTick - _tick)
					* SequencingConstants.Diachron.TotalSeconds / _root.State.Tempo);
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
				_tick += delta.TotalSeconds * _root.State.Tempo
					/ SequencingConstants.Diachron.TotalSeconds;
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
			Elapsed = nextTickTime;
			_tick = current.DueTick;
			CheckCooperationBudget();
			if (current.Complete)
			{
				_active.Remove(current);
				current.Dispose();
				continue;
			}
			if (TryOperate(current, out result))
				return true;
		}
		return false;
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
			// Evaluate eligible timing only at this invocation's new row
			// boundary. Resolve against the common processor's command
			// semantics; no timing state changed while this was pending.
			NoteScheduleBuilder resolvedTiming = new();
			PatternNoteProcessor.GenerateNotes(
				new SingleEventSlice(timing with
				{
					Offset = MusicalTime.Zero,
				}), current.Context, resolvedTiming, out _);
			current.ConsumeTiming();
			NoteEvent[] due = resolvedTiming.Freeze().ToArray();
			if (due.Length != 1 || due[0].Offset.TimeOffset != TimeSpan.Zero)
				throw new NotSupportedException(
					"Deferred timing did not resolve to one immediate boundary command.");
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
			foreach (NoteEvent note in resolved.Freeze())
			{
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
			NoteEvent emitted = immediate[0] with
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
	}
}
