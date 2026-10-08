using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Experimental flattened Pattern/Sequence invocation coordinator. All
/// invocations share exactly one incremental musical clock. A nested
/// StartNote is consumed only if it resolves to a Pattern or Sequence;
/// ordinary and mixdown starts stay with the renderer. No eager recursion.
/// </summary>
public sealed class IncrementalRecursiveTimeline : IDisposable
{
	private const int MaximumCooperationPerStep = 8192;
	private const int MaximumDepth = 128;

	private sealed class Invocation(
		long id, ObjectId sourceId, SequencingContext context, long? parentId)
	{
		public long Id { get; } = id;
		public ObjectId SourceId { get; } = sourceId;
		public SequencingContext Context { get; } = context;
		public long? ParentId { get; } = parentId;
		public HashSet<long> Children { get; } = [];

		// Pattern frames own one timeline cursor; Sequence frames own an
		// ordered list of child Pattern frames, created one at a time.
		public long? PatternId { get; set; }
		public List<SequenceEntry>? Entries { get; set; }
		public IEnumerator<RawSequenceStep>? ScriptOrders { get; set; }
		public bool ScriptEnded { get; set; } = true;
		public int ScriptCooperationsAtTick { get; set; }
		public double LastCooperationTick { get; set; } = double.NegativeInfinity;
		public int Order { get; set; }
		public int? StartRowOverride { get; set; }
		public PatternFlowControl PendingFlow { get; set; } = PatternFlowControl.None;
		public bool OrdersEnded { get; set; }
		public long? OrderFrameId { get; set; }
		public Func<SequenceOrderJumpEncounter, bool>? FollowJump { get; set; }
		public int Depth { get; set; }
		public bool IsSequence => Entries is not null;
	}

	private readonly SequencingContext _root;
	private readonly IIncrementalInvocationResolver _resolver;
	private readonly IIncrementalScriptSourceCompiler? _scripts;
	private readonly IncrementalPatternTimeline _timeline;
	private readonly Dictionary<long, Invocation> _frames = [];
	private readonly Dictionary<long, long> _patternOwners = [];
	private long _nextFrameId;
	private bool _disposed;

	public IncrementalRecursiveTimeline(
		SequencingContext root,
		IIncrementalInvocationResolver resolver,
		IIncrementalScriptSourceCompiler? scripts = null)
	{
		_root = root ?? throw new ArgumentNullException(nameof(root));
		_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
		_scripts = scripts;
		_timeline = new IncrementalPatternTimeline(root);
	}

	public TimeSpan Elapsed => _timeline.Elapsed;
	public double Tick => _timeline.Tick;
	public bool IsComplete => _frames.Count == 0 && _timeline.IsComplete;

	/// <summary>
	/// Begin a top-level Pattern or data Sequence at the current shared
	/// musical position. A root may be added while another root is running.
	/// </summary>
	public long AddRoot(ObjectId sourceId, int startOrder = 0,
		int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (startOrder < 0 || startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startOrder));
		if (!_resolver.TryResolve(sourceId, out SongObject? source) || source is null)
			throw new ArgumentException("Root source could not be resolved.", nameof(sourceId));
		return AddInvocation(source, _root, null, startOrder, startRow,
			shouldFollowOrderJump);
	}

	public bool IsInvocationActive(long invocationId)
		=> _frames.ContainsKey(invocationId);

	/// <summary>
	/// Cancels a subtree immediately. Pending wall deadlines, future orders
	/// and active raw enumerators of descendants are all discarded, while
	/// unrelated sibling invocations continue.
	/// </summary>
	public bool Cancel(long invocationId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_frames.ContainsKey(invocationId))
			return false;
		RemoveSubtree(invocationId);
		return true;
	}

	private void RemoveSubtree(long invocationId)
	{
		Invocation frame = _frames[invocationId];
		frame.ScriptOrders?.Dispose();
		frame.ScriptOrders = null;
		foreach (long child in frame.Children.ToArray())
			RemoveSubtree(child);
		if (frame.PatternId is long pattern)
		{
			_timeline.Cancel(pattern);
			_patternOwners.Remove(pattern);
		}
		_frames.Remove(invocationId);
		if (frame.ParentId is long parentId && _frames.TryGetValue(
			parentId, out Invocation? parent))
			parent.Children.Remove(invocationId);
	}

	private long AddInvocation(SongObject source,
		SequencingContext context, long? parentId,
		int startOrder = 0, int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? followJump = null)
	{
		int depth = 0;
		for (long? id = parentId; id.HasValue;)
		{
			Invocation ancestor = _frames[id.Value];
			if (ancestor.SourceId == source.Id)
				throw new InvalidOperationException(
					$"Flattened sound source cycle includes object {source.Id.Value}.");
			depth++;
			id = ancestor.ParentId;
		}
		if (depth >= MaximumDepth)
			throw new InvalidOperationException(
				"Flattened invocation nesting exceeded the depth limit.");

		Invocation frame = new(_nextFrameId++, source.Id, context, parentId)
		{
			Depth = depth,
			FollowJump = followJump,
		};
		switch (source)
		{
			case ScriptPatternDefinition script:
				if (startOrder != 0)
					throw new ArgumentOutOfRangeException(nameof(startOrder));
				frame.PatternId = _timeline.Add(
					RequireScriptCompiler().CompilePattern(script),
					script.RowCount, context, startRow ?? 0);
				break;
			case PatternDefinition pattern
				when pattern is IIncrementalRawPatternNoteGenerator raw:
				if (startOrder != 0)
					throw new ArgumentOutOfRangeException(nameof(startOrder));
				frame.PatternId = _timeline.Add(
					raw, pattern.RowCount, context, startRow ?? 0);
				break;
			case DataSequenceDefinition sequence:
				frame.Entries = sequence.Entries.ToList();
				frame.Order = startOrder;
				frame.StartRowOverride = startRow;
				frame.OrdersEnded = startOrder >= frame.Entries.Count;
				break;
			case ScriptSequenceDefinition script:
				frame.Entries = [];
				frame.Order = startOrder;
				frame.StartRowOverride = startRow;
				frame.ScriptOrders = RequireScriptCompiler()
					.CompileSequence(script).EnumerateRawSteps(context).GetEnumerator();
				frame.ScriptEnded = false;
				break;
			default:
				throw new NotSupportedException(
					$"Source {source.Id.Value} needs an incremental Pattern or Sequence.");
		}
		_frames.Add(frame.Id, frame);
		if (frame.PatternId is long patternId)
			_patternOwners.Add(patternId, frame.Id);
		if (parentId is long parent)
			_frames[parent].Children.Add(frame.Id);
		return frame.Id;
	}

	private IIncrementalScriptSourceCompiler RequireScriptCompiler()
		=> _scripts ?? throw new NotSupportedException(
			"Roslyn scripted sources require an explicitly configured incremental script compiler.");

	private long? EnterNextOrder(Invocation sequence)
	{
		if (sequence.Entries is null || sequence.OrdersEnded)
			return null;
		if (sequence.Order >= sequence.Entries.Count)
		{
			sequence.OrdersEnded = sequence.ScriptEnded;
			return null;
		}

		SequenceEntry entry = sequence.Entries[sequence.Order];
		int startRow = sequence.StartRowOverride ?? entry.StartRow;
		sequence.StartRowOverride = null;
		if (!_resolver.TryResolve(entry.PatternId, out SongObject? source)
			|| source is null)
		{
			sequence.Order++;
			sequence.OrdersEnded = sequence.ScriptEnded
				&& sequence.Order >= sequence.Entries.Count;
			return null;
		}
		if (source is not PatternDefinition)
			throw new NotSupportedException(
				$"Sequence order {sequence.Order} requires an incremental Pattern.");
		return AddInvocation(source, sequence.Context, sequence.Id,
			startRow: startRow);
	}

	private void CompleteOrder(Invocation sequence)
	{
		PatternFlowControl flow = sequence.PendingFlow;
		sequence.PendingFlow = PatternFlowControl.None;
		SequenceEntry current = sequence.Entries![sequence.Order];
		if (flow.OrderJump.HasValue && flow.SourceRow.HasValue
			&& sequence.FollowJump is not null
			&& !sequence.FollowJump(new SequenceOrderJumpEncounter(
				current.PatternId, flow.SourceRow.Value, flow.OrderJump.Value)))
		{
			sequence.OrdersEnded = true;
			return;
		}
		sequence.Order = flow.OrderJump ?? checked(sequence.Order + 1);
		sequence.StartRowOverride = flow.BreakRow;
		sequence.OrdersEnded = sequence.ScriptEnded
			&& sequence.Order >= sequence.Entries.Count;
	}

	private IncrementalPatternTimelineStep.Cooperate? PrepareSequences()
	{
		foreach (Invocation sequence in _frames.Values
			.Where(f => f.IsSequence && !f.OrdersEnded)
			.OrderBy(f => f.Depth).ThenBy(f => f.Id).ToArray())
		{
			// Exactly one direct Pattern order at a time, but older
			// orders may retain delayed notes or descendants.
			Invocation? active = sequence.Children
				.Select(id => _frames[id])
				.FirstOrDefault(f => f.PatternId is long pid
					&& _timeline.HasUnfinishedRows(pid));
			if (active is not null)
				continue;

			// An order just finished: flow is committed at its actual
			// ending tick, before the next order is instantiated.
			if (sequence.Entries is null)
				continue;
			if (sequence.Children.Any(id => _frames[id].PatternId.HasValue
				&& _frames[id].Id == sequence.OrderFrameId))
			{
				CompleteOrder(sequence);
				sequence.OrderFrameId = null;
			}
			if (!sequence.OrdersEnded)
			{
				if (sequence.Order >= sequence.Entries.Count
					&& !sequence.ScriptEnded)
				{
					IEnumerator<RawSequenceStep> source = sequence.ScriptOrders
						?? throw new InvalidOperationException(
							"Scripted Sequence iterator is unavailable.");
					if (!source.MoveNext())
					{
						source.Dispose();
						sequence.ScriptOrders = null;
						sequence.ScriptEnded = true;
					}
					else
					{
						switch (source.Current)
						{
							case RawSequenceStep.Play play when play.Entry is not null:
								sequence.Entries.Add(play.Entry);
								break;
							case RawSequenceStep.Cooperate:
								// CPU cooperation cannot advance the shared musical
								// clock or commit future Pattern state.
								if (Math.Abs(Tick - sequence.LastCooperationTick) > 1e-9)
								{
									sequence.LastCooperationTick = Tick;
									sequence.ScriptCooperationsAtTick = 0;
								}
								if (++sequence.ScriptCooperationsAtTick >
									MaximumCooperationPerStep)
									throw new InvalidOperationException(
										"Recursive scripted Sequence exceeded its same-tick CPU cooperation budget.");
								return new IncrementalPatternTimelineStep.Cooperate(
									sequence.Id, Tick, Elapsed);
							default:
								throw new InvalidOperationException(
									"Scripted Sequence emitted an invalid raw step.");
						}
					}
				}
				// Only the freshly created Pattern represents this order.
				// Never revive a finished historical frame when the script
				// exhausts, skips an unresolved entry, or requests a
				// not-yet-generated future order.
				sequence.OrderFrameId = EnterNextOrder(sequence);
			}
		}
		return null;
	}

	/// <summary>
	/// Returns the next shared-clock event or cooperative Advance/Flow.
	/// Nested flattened starts are consumed and replaced by live child
	/// invocations; any unrelated commands in the same event are retained.
	/// </summary>
	public bool TryStep(out IncrementalPatternTimelineStep? result)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		result = null;
		for (int budget = 0; budget < MaximumCooperationPerStep; budget++)
		{
			if (PrepareSequences() is { } checkpoint)
			{
				result = checkpoint;
				return true;
			}
			PruneCompleted();
			if (_timeline.TryStep(out IncrementalPatternTimelineStep? step))
			{
				switch (step)
				{
					case IncrementalPatternTimelineStep.Flow flow:
						if (_patternOwners.TryGetValue(flow.InvocationId, out long owner)
							&& _frames[owner].ParentId is long parent
							&& _frames.TryGetValue(parent, out Invocation? sequence)
							&& sequence.IsSequence)
							sequence.PendingFlow = flow.Control;
						result = flow;
						return true;
					case IncrementalPatternTimelineStep.Emit emit:
						if (!_patternOwners.TryGetValue(emit.InvocationId, out long frameId)
							|| !_frames.TryGetValue(frameId, out Invocation? frame))
							throw new InvalidOperationException(
								"Incremental emitted event has no owning invocation.");
						List<NoteCommand> retained = [];
						foreach (NoteCommand command in emit.Note.Commands)
						{
							if (command is not StartNoteCommand start || start.Mixdown
								|| !_resolver.TryResolve(start.SourceId, out SongObject? source)
								|| source is not (PatternDefinition or SequenceDefinition))
							{
								retained.Add(command);
								continue;
							}
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new NotSupportedException(
									"Flattened child requires a physical parent channel.");
							if (start.PitchMultiplier != 1.0
								|| start.PlaybackSpeedMultiplier != 1.0
								|| start.Volume.HasValue)
								throw new NotSupportedException(
									"Transformed flattened child notes are not supported.");

							int physicalOffset = emit.Note.Target.PhysicalChannel
								- frame.Context.PhysicalChannelBase;
							if (physicalOffset < 0)
								throw new InvalidOperationException(
									"Child channels cannot precede parent channel space.");
							SequencingContext child = frame.Context.FlattenedChild(
								physicalChannelOffset: physicalOffset);
							child.TimelineOrigin = emit.Time;
							AddInvocation(source, child, frame.Id);
						}
						if (retained.Count == 0)
							continue;
						result = emit with
						{
							Note = emit.Note with { Commands = retained.ToArray() },
						};
						return true;
					default:
						result = step;
						return true;
				}
			}
			if (PrepareSequences() is { } checkpointAfter)
			{
				result = checkpointAfter;
				return true;
			}
			PruneCompleted();
			if (IsComplete)
				return false;
		}
		throw new InvalidOperationException(
			"Recursive timeline exceeded its same-step cooperation budget.");
	}

	private void PruneCompleted()
	{
		bool removed;
		do
		{
			removed = false;
			foreach (Invocation frame in _frames.Values
				.OrderByDescending(f => f.Depth).ThenByDescending(f => f.Id).ToArray())
			{
				if (frame.Children.Count != 0
					|| frame.IsSequence && !frame.OrdersEnded
					|| frame.PatternId is long pattern
						&& _timeline.HasOutstandingWork(pattern)
					|| frame.ParentId is long parent
						&& _frames.TryGetValue(parent, out Invocation? seq)
						&& seq.OrderFrameId == frame.Id)
					continue;
				RemoveSubtree(frame.Id);
				removed = true;
			}
		} while (removed);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timeline.Dispose();
		foreach (Invocation frame in _frames.Values)
			frame.ScriptOrders?.Dispose();
		_frames.Clear();
		_patternOwners.Clear();
	}
}
