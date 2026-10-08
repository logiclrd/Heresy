using System;
using System.Collections.Generic;
using System.Linq;

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
	private const double TickTolerance = 1e-9;

	private readonly List<SequenceEntry> _entries;
	private IEnumerator<RawSequenceStep>? _scriptEnumerator;
	private bool _sourceEnded;
	private double _lastCooperationTick = double.NegativeInfinity;
	private int _cooperationAtTick;
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

		_entries = entries.ToList();
		_sourceEnded = true;
		_resolver = resolver;
		_context = context;
		_timeline = new IncrementalPatternTimeline(context);
		_initialOrigin = context.TimelineOrigin;
		_shouldFollowOrderJump = shouldFollowOrderJump;
		_order = startOrder;
		_startRowOverride = startRow;
		_ordersEnded = startOrder >= _entries.Count;
	}

	/// <summary>
	/// Consume scripted Play instructions only when an order is required.
	/// The iterator's local variables and RNG remain suspended across
	/// entire child Pattern visits, and CPU checkpoints do not move time.
	/// Previously generated entries remain available for Bxx revisits.
	/// </summary>
	public IncrementalSequenceCursor(
		IIncrementalRawSequenceEntryGenerator generator,
		ISequencePatternResolver resolver,
		SequencingContext context,
		int startOrder = 0,
		int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
		: this(Array.Empty<SequenceEntry>(), resolver, context,
			startOrder, startRow, shouldFollowOrderJump)
	{
		ArgumentNullException.ThrowIfNull(generator);
		_scriptEnumerator = generator.EnumerateRawSteps(context).GetEnumerator();
		_sourceEnded = false;
		_ordersEnded = false;
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
				if (!StartNextOrder(out IncrementalPatternTimelineStep? checkpoint))
				{
					result = checkpoint;
					return true;
				}
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

	private bool StartNextOrder(out IncrementalPatternTimelineStep? checkpoint)
	{
		checkpoint = null;

		// Generate at most one script step per dispatch iteration. A
		// cooperative pause must be observable without draining the
		// script, and empty/missing orders are guarded by TryStep's
		// finite same-instant work budget.
		if (_order >= _entries.Count && !_sourceEnded)
		{
			if (_scriptEnumerator is null)
				throw new InvalidOperationException("Sequence entry iterator is unavailable.");
			if (_scriptEnumerator.MoveNext())
			{
				switch (_scriptEnumerator.Current)
				{
					case RawSequenceStep.Play play when play.Entry is not null:
						_entries.Add(play.Entry);
						break;
					case RawSequenceStep.Cooperate:
						CheckScriptCooperationBudget();
						checkpoint = new IncrementalPatternTimelineStep.Cooperate(
							-1, Tick, Elapsed);
						return false;
					default:
						throw new InvalidOperationException(
							"Scripted Sequence emitted an invalid raw step.");
				}
			}
			else
			{
				_sourceEnded = true;
				_scriptEnumerator.Dispose();
				_scriptEnumerator = null;
			}
		}

		if (_order >= _entries.Count)
		{
			_ordersEnded = _sourceEnded;
			return true;
		}

		// Advancing Bxx loops are legitimate indefinite playback. A hard
		// total-visit cap would eventually stop a song that loops for hours
		// or days, so resource safety is handled by TryStep's bounded
		// cooperation work and the Pattern timeline's same-tick budget.
		if (_visits < long.MaxValue)
			_visits++;

		SequenceEntry entry = _entries[_order];
		int startRow = _startRowOverride ?? entry.StartRow;
		_startRowOverride = null;

		if (!_resolver.TryResolve(entry.PatternId,
				out IRawPatternNoteGenerator? resolved) || resolved is null)
		{
			_order++;
			return true;
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
		return true;
	}

	private void CheckScriptCooperationBudget()
	{
		// This persists across TryStep calls. Returning control without
		// musical progress is useful, but an infinite CPU-only loop must
		// not permanently starve playback at one tracker instant.
		if (Math.Abs(Tick - _lastCooperationTick) > TickTolerance)
		{
			_lastCooperationTick = Tick;
			_cooperationAtTick = 0;
		}
		if (++_cooperationAtTick > MaximumCooperationPerStep)
			throw new InvalidOperationException(
				"Incremental Sequence exceeded its same-tick CPU cooperation budget.");
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
		_ordersEnded = _sourceEnded && _order >= _entries.Count;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_scriptEnumerator?.Dispose();
		_scriptEnumerator = null;
		_timeline.Dispose();
	}
}
