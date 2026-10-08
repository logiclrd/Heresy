using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Experimental, single-Sequence cursor over the incremental shared-clock
/// Pattern timeline. Resolves only the next order; an order jump revisits
/// the Pattern by constructing a fresh Pattern cursor, not by precompiling
/// any number of future order visits. It does not replace production
/// SequenceNoteProcessor or the song's recursive scheduler.
/// </summary>
public sealed class IncrementalSequenceCursor : IDisposable
{
	private const int MaximumCooperationPerStep = 8192;

	private readonly SequenceEntry[] _entries;
	private readonly ISequencePatternResolver _resolver;
	private readonly SequencingContext _context;
	private readonly IncrementalPatternTimeline _timeline;
	private readonly Func<SequenceOrderJumpEncounter, bool>? _shouldFollowOrderJump;
	private readonly TimeSpan _initialOrigin;
	private TimeSpan _currentEntryOrigin;
	private long? _currentInvocation;
	private PatternFlowControl _pendingFlow;
	private int _order;
	private int? _startRowOverride;
	private long _visits;
	private bool _ordersEnded;
	private bool _disposed;

	public IncrementalSequenceCursor(
		DataSequenceDefinition sequence,
		ISequencePatternResolver resolver,
		SequencingContext context,
		int startOrder = 0,
		int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
		: this(
			sequence?.Entries
				?? throw new ArgumentNullException(nameof(sequence)),
			resolver, context, startOrder, startRow, shouldFollowOrderJump)
	{
	}

	public IncrementalSequenceCursor(
		IReadOnlyList<SequenceEntry> entries,
		ISequencePatternResolver resolver,
		SequencingContext context,
		int startOrder = 0,
		int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(context);
		if (startOrder < 0)
			throw new ArgumentOutOfRangeException(nameof(startOrder));
		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		_entries = entries.ToArray();
		_resolver = resolver;
		_context = context;
		_timeline = new IncrementalPatternTimeline(context);
		_initialOrigin = context.TimelineOrigin;
		_shouldFollowOrderJump = shouldFollowOrderJump;
		_order = startOrder;
		_startRowOverride = startRow;
		_ordersEnded = startOrder >= _entries.Length;
	}

	/// <summary>The order currently executing, or the next order to visit.</summary>
	public int Order => _order;
	public TimeSpan Elapsed => _timeline.Elapsed;
	public double Tick => _timeline.Tick;
	public long Visits => _visits;
	public bool IsComplete => _ordersEnded
		&& _currentInvocation is null && _timeline.IsComplete;

	/// <summary>
	/// Cooperatively emits the next note, Pattern-row advance, or Pattern
	/// flow request. A Bxx loop with positive musical duration may continue
	/// indefinitely, but TryStep performs only finite work per call.
	/// </summary>
	public bool TryStep(out IncrementalPatternTimelineStep? result)
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(IncrementalSequenceCursor));
		result = null;

		for (int work = 0; work < MaximumCooperationPerStep; work++)
		{
			if (_currentInvocation is long current
				&& !_timeline.HasUnfinishedRows(current))
				FinishCurrentOrder();

			if (_currentInvocation is null && !_ordersEnded)
			{
				StartNextOrder();
				continue;
			}

			// Pattern source iterators which depend on TimelineOrigin see
			// the same invocation origin as in the eager Sequence processor.
			TimeSpan previousOrigin = _context.TimelineOrigin;
			try
			{
				_context.TimelineOrigin = _initialOrigin + _currentEntryOrigin;
				if (_timeline.TryStep(out result))
				{
					if (result is IncrementalPatternTimelineStep.Flow flow
						&& flow.InvocationId == _currentInvocation)
						_pendingFlow = flow.Control;
					return true;
				}
			}
			finally
			{
				_context.TimelineOrigin = previousOrigin;
			}

			if (_currentInvocation is null)
				return false;

			if (_timeline.HasUnfinishedRows(_currentInvocation.Value))
				throw new InvalidOperationException(
					"Incremental Pattern stopped without completing its active source rows.");
		}

		throw new InvalidOperationException(
			"Incremental Sequence exceeded its zero-progress order/cooperation budget.");
	}

	private void StartNextOrder()
	{
		if ((uint)_order >= (uint)_entries.Length)
		{
			_ordersEnded = true;
			return;
		}

		if (++_visits > NoteScheduleBuilder.MaximumGeneratedNotes)
			throw new SequencingResourceLimitException(
				$"Sequence control exceeded {NoteScheduleBuilder.MaximumGeneratedNotes:N0} pattern visits.");

		SequenceEntry entry = _entries[_order];
		int startRow = _startRowOverride ?? entry.StartRow;
		_startRowOverride = null;

		if (!_resolver.TryResolve(entry.PatternId,
				out IRawPatternNoteGenerator? resolved) || resolved is null)
		{
			_order++;
			return;
		}

		// A Pattern's finite row count cannot be safely inferred by
		// eagerly running its raw generator: doing so would defeat lazy
		// sequencing and could execute stateful scripts ahead of time.
		if (resolved is not PatternDefinition definition
			|| resolved is not IIncrementalRawPatternNoteGenerator generator)
			throw new NotSupportedException(
				$"Sequence order {_order} requires an incremental PatternDefinition with a known RowCount.");

		_currentEntryOrigin = Elapsed;
		_pendingFlow = PatternFlowControl.None;
		_currentInvocation = _timeline.Add(
			generator, definition.RowCount, _context, startRow);
	}

	private void FinishCurrentOrder()
	{
		_currentInvocation = null;
		PatternFlowControl flow = _pendingFlow;
		_pendingFlow = PatternFlowControl.None;

		if (flow.OrderJump.HasValue
			&& flow.SourceRow.HasValue
			&& _shouldFollowOrderJump is not null)
		{
			SequenceEntry current = _entries[_order];
			SequenceOrderJumpEncounter encounter = new(
				current.PatternId, flow.SourceRow.Value, flow.OrderJump.Value);
			if (!_shouldFollowOrderJump(encounter))
			{
				_ordersEnded = true;
				return;
			}
		}

		_order = flow.OrderJump ?? checked(_order + 1);
		_startRowOverride = flow.BreakRow;
		_ordersEnded = (uint)_order >= (uint)_entries.Length;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timeline.Dispose();
	}
}
