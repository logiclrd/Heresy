using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Core.Patterns;

/// <summary>
/// One resolved row of an incrementally executed Pattern. Events carry
/// absolute offsets from this Pattern invocation's origin; Duration is the
/// length of the row just executed, not the length of the whole Pattern.
/// </summary>
public sealed record IncrementalPatternRow(
	int Row,
	TimeSpan Origin,
	TimeSpan Duration,
	IReadOnlyList<NoteEvent> Events);

/// <summary>
/// First incremental consumer of Pattern-local raw steps. Invocations retain
/// their raw iterator and resolve only the requested row via the established
/// PatternNoteProcessor, so shared Source and tracker-effect state is not
/// executed during preparation of later rows.
///
/// This initial slice operates at whole-row boundaries and is not a complete
/// concurrent scheduler. Fractional scripted events, delayed global effects,
/// independent nested cursors and mid-row external timing changes need their
/// own incremental scheduling contract before replacing song compilation.
/// </summary>
public sealed class IncrementalPatternNoteProcessor : IDisposable
{
	private sealed class RowSlice : IDeferredSourcePatternGenerator
	{
		private readonly IReadOnlyList<NoteEvent> _events;

		public RowSlice(IReadOnlyList<NoteEvent> events) => _events = events;

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			foreach (NoteEvent note in _events)
			{
				output.Append(note with
				{
					Offset = new MusicalTime(note.Offset.TimeOffset, 0),
				});
			}
			rowCount = 1;
		}
	}

	private readonly SequencingContext _context;
	private readonly IEnumerator<RawPatternStep> _source;
	private readonly int _rowCount;
	private RawPatternStep? _pending;
	private bool _sourceEnded;
	private bool _disposed;
	private double _lastSourcePosition;
	private long _nextEmissionOrder;

	public IncrementalPatternNoteProcessor(
		IIncrementalRawPatternNoteGenerator generator,
		SequencingContext context,
		int rowCount,
		int startRow = 0)
	{
		ArgumentNullException.ThrowIfNull(generator);
		ArgumentNullException.ThrowIfNull(context);
		if (rowCount < 0)
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));
		if (context.FlattenedSourceExpander is not null
			|| context.IsPreparingFlattenedChild)
		{
			throw new NotSupportedException(
				"Incremental row processor does not yet support eager flattened-source expansion.");
		}

		_context = context;
		_rowCount = rowCount;
		NextRow = Math.Min(rowCount, startRow);
		// Raw data Pattern translation must not resolve Source-column memory
		// just because a future raw note was inspected for lookahead.
		_context.ResolvePatternSourcesAtRowTime = true;
		_source = generator.EnumerateRawSteps(context).GetEnumerator();
	}

	public int NextRow { get; private set; }
	public TimeSpan Elapsed { get; private set; }
	public bool IsComplete => NextRow >= _rowCount;

	/// <summary>
	/// Executes exactly one row at the current shared sequencing state.
	/// The caller controls *when* this method runs, making intervening
	/// Tempo/Speed or channel-state changes visible only to future rows.
	/// </summary>
	public bool TryAdvance(out IncrementalPatternRow? result)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		result = null;
		if (IsComplete)
			return false;

		int row = NextRow;
		List<NoteEvent> rawEvents = [];

		while (TryGetPending(out RawPatternStep? step))
		{
			if (step!.Row >= row + 1.0)
				break;
			_pending = null;
			if (step is RawPatternStep.Emit emit && step.Row >= row)
				rawEvents.Add(emit.Note);
			// Advances are cooperative markers, never note commands.
		}

		NoteScheduleBuilder builder = new();
		PatternNoteProcessor.GenerateNotes(
			new RowSlice(rawEvents), _context, builder, out TimeSpan duration);
		TimeSpan origin = Elapsed;
		List<NoteEvent> events = [];
		foreach (NoteEvent note in builder.Freeze())
		{
			events.Add(note with
			{
				Offset = new MusicalTime(origin + note.Offset.TimeOffset, 0),
				EmissionOrder = _nextEmissionOrder++,
			});
		}
		Elapsed += duration;
		NextRow++;
		result = new IncrementalPatternRow(row, origin, duration, events);
		return true;
	}

	private bool TryGetPending(out RawPatternStep? step)
	{
		int discarded = 0;
		while (_pending is null && !_sourceEnded)
		{
			if (!_source.MoveNext())
			{
				_sourceEnded = true;
				break;
			}
			RawPatternStep next = _source.Current
				?? throw new InvalidOperationException(
					"A raw Pattern iterator yielded null.");
			if (!double.IsFinite(next.Row) || next.Row < 0)
				throw new InvalidOperationException(
					"A raw Pattern iterator must have finite nonnegative positions.");
			if (next is RawPatternStep.Emit && next.Row < _lastSourcePosition)
			{
				_context.Diagnostics.ReportDroppedOutOfOrderNote(
					next.Row, _lastSourcePosition);
				// No musical rewind: silently skip already-passed notes.
				if (++discarded > 8192)
					throw new InvalidOperationException(
						"A raw Pattern iterator exceeded its no-progress budget.");
				continue;
			}
			if (next is not RawPatternStep.Cooperate
				&& next.Row < _lastSourcePosition)
				throw new InvalidOperationException(
					"Raw Pattern progress markers must be nondecreasing.");
			if (next is not RawPatternStep.Cooperate)
				_lastSourcePosition = next.Row;
			_pending = next;
		}
		step = _pending;
		return step is not null;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_source.Dispose();
	}
}
