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

	private sealed class Cursor : IDisposable
	{
		private readonly IEnumerator<RawPatternStep> _source;
		private RawPatternStep? _lookahead;
		private bool _ended;
		private double _lastRow;
		private IReadOnlyList<NoteEvent> _rowEvents = [];
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
		public NoteEvent? DueEvent =>
			_inRow && _eventIndex < _rowEvents.Count
				? _rowEvents[_eventIndex] : null;

		public void BeginRow(double tick)
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
					events.Add(emit.Note);
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
			NoteEvent consumed = _rowEvents[_eventIndex++];
			// Speed changes at this cursor's row boundary resize only its
			// *own* captured row; existing rows of other cursors stay intact.
			if (IsTiming(consumed)
				&& DuePosition(consumed, Row, RowCount) == Row
				&& consumed.Commands.Any(c => c is SetSpeedCommand))
				_rowSpeed = Context.State.Speed;
			RefreshDue();
		}

		public void FinishRow()
		{
			if (!_inRow || DueEvent is not null)
				throw new InvalidOperationException("Cursor row is not finished.");
			_inRow = false;
			_rowEvents = [];
			Row++;
			// The next row is eligible at exactly this tick. The caller
			// decides when it executes relative to other due cursors.
		}

		private void RefreshDue()
		{
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
			=> note.Target.Kind == ChannelTargetKind.Global
				&& note.Commands.Count != 0
				&& note.Commands.All(c => c is SetTempoCommand or SetSpeedCommand);

		private static void Validate(NoteEvent note)
		{
			if (note.Offset.TimeOffset != TimeSpan.Zero)
				throw new NotSupportedException(
					"The incremental tick merger does not yet support fixed wall-time offsets.");
			if (note.Target.Kind is not (ChannelTargetKind.Physical
				or ChannelTargetKind.Global))
				throw new NotSupportedException(
					"The incremental tick merger does not yet support virtual targets.");

			foreach (NoteCommand command in note.Commands)
			{
				bool allowed = note.Target.Kind == ChannelTargetKind.Global
					? command is SetTempoCommand or SetSpeedCommand
					: command is StartNoteCommand or NoteOffCommand
						or NoteCutCommand or SelectPatternSourceCommand;
				if (!allowed)
					throw new NotSupportedException(
						$"The incremental tick merger does not yet support {command.GetType().Name}.");
			}

			if (!double.IsFinite(note.Offset.RowOffset)
				|| note.Offset.RowOffset < 0)
				throw new InvalidOperationException("Invalid raw musical position.");
		}
	}

	private readonly SequencingContext _root;
	private readonly List<Cursor> _active = [];
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
	public bool IsComplete => _active.Count == 0;

	/// <summary>
	/// Starts an independent invocation at the current musical instant.
	/// A flattened child may pass a separately mapped SequencingContext,
	/// provided it shares the root's actual clock and channel-state map.
	/// </summary>
	public void Add(
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
		_active.Add(new Cursor(generator, context, rowCount,
			startRow, _tick, _nextSequence++));
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
		while (_active.Count != 0)
		{
			Cursor current = _active
				.OrderBy(c => c.DueTick)
				.ThenBy(c => c.Context.PhysicalChannelBase)
				.ThenBy(c => c.Sequence)
				.First();
			if (current.DueTick < _tick - TickTolerance)
				throw new InvalidOperationException("Incremental cursor moved backwards.");
			double next = current.DueTick;
			Elapsed += TimeSpan.FromSeconds(
				Math.Max(0, next - _tick)
				* SequencingConstants.Diachron.TotalSeconds / _root.State.Tempo);
			_tick = next;
			if (Math.Abs(_tick - _lastStepTick) > TickTolerance)
			{
				_lastStepTick = _tick;
				_operationsAtTick = 0;
			}
			if (++_operationsAtTick > MaximumOperationsAtOneTick)
				throw new InvalidOperationException(
					"Incremental scheduler exceeded its same-tick cooperation budget.");

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

	private bool TryOperate(Cursor current, out IncrementalPatternTimelineStep? result)
	{
		result = null;
		if (!current.InRow)
		{
			current.BeginRow(_tick);
			return false;
		}
		if (current.DueEvent is { } raw)
		{
			NoteScheduleBuilder resolved = new();
			PatternNoteProcessor.GenerateNotes(
				new SingleEventSlice(raw), current.Context, resolved,
				out _);
			current.ConsumeEvent();
			List<NoteEvent> emitted = resolved.Freeze().ToList();
			if (emitted.Count > 1)
				throw new NotSupportedException(
					"One raw cursor event resolved into several playback operations.");
			if (emitted.Count == 0)
				return false;
			NoteEvent note = emitted[0] with
			{
				Offset = new MusicalTime(Elapsed, 0),
				EmissionOrder = _emissionOrder++,
			};
			result = new IncrementalPatternTimelineStep.Emit(note, _tick, Elapsed);
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
	}
}
