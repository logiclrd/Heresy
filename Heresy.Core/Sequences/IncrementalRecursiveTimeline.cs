using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Streaming flattened Pattern/Sequence invocation coordinator. All
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
		public ISequenceEntryProvider? EntrySource { get; set; }
		public int AbsoluteIndex { get; set; }
		public int PreviousSequenceIndex { get; set; } = -1;
		public SequenceEntry? ActiveEntry { get; set; }
		public int Order { get; set; }
		public int? StartRowOverride { get; set; }
		public PatternFlowControl PendingFlow { get; set; } = PatternFlowControl.None;
		public bool OrdersEnded { get; set; }
		public long? OrderFrameId { get; set; }
		public Func<SequenceOrderJumpEncounter, bool>? FollowJump { get; set; }
		public int Depth { get; set; }
		public bool IsSequence => EntrySource is not null;
	}

	private readonly SequencingContext _root;
	private readonly IIncrementalInvocationResolver _resolver;
	private readonly IIncrementalScriptSourceCompiler? _scripts;
	private readonly IncrementalPatternTimeline _timeline;
	private readonly Dictionary<long, Invocation> _frames = [];
	private readonly Dictionary<long, long> _patternOwners = [];
	private long _nextFrameId;
	private long _nextPlaybackOwner = 1;
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
		_timeline.RowBegan += ReportRowBegan;
	}

	/// <summary>Realtime view follows rows as cursors enter them;
	/// no future sequence orders are traversed to build this feed.</summary>
	public event Action<ObjectId, int, ObjectId?, int?, TimeSpan>? RowBegan;

	private void ReportRowBegan(long cursorId, int row, TimeSpan time)
	{
		if (!_patternOwners.TryGetValue(cursorId, out long owner)
			|| !_frames.TryGetValue(owner, out Invocation? frame))
			return;
		ObjectId? sequenceId = null;
		int? order = null;
		if (frame.ParentId is long parent
			&& _frames.TryGetValue(parent, out Invocation? ancestor)
			&& ancestor.IsSequence)
		{
			sequenceId = ancestor.SourceId;
			order = ancestor.Order;
		}
		RowBegan?.Invoke(frame.SourceId, row, sequenceId, order, time);
	}

	/// <summary>Optional live playback source of remembered note volume.
	/// The renderer includes note-volume slides and NNA, which cannot be
	/// inferred from raw sequencing effect memory alone.</summary>
	public Func<long, int, TimeSpan, double>? ReadRememberedNoteVolume { get; set; }

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
		=> Cancel(invocationId, null);

	/// <summary>
	/// Explicitly cancel a subtree and optionally collect every invocation
	/// removed (including descendants) for renderer-owned voice cancellation.
	/// Natural completion never publishes these cancellation identities.
	/// </summary>
	public bool Cancel(long invocationId, ICollection<long>? canceledInvocations)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_frames.ContainsKey(invocationId))
			return false;
		RemoveSubtree(invocationId, canceledInvocations);
		return true;
	}

	private void RemoveSubtree(long invocationId,
		ICollection<long>? canceledInvocations = null)
	{
		Invocation frame = _frames[invocationId];
		foreach (long child in frame.Children.ToArray())
			RemoveSubtree(child, canceledInvocations);
		canceledInvocations?.Add(invocationId);
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
				frame.EntrySource = sequence;
				frame.Order = startOrder;
				frame.StartRowOverride = startRow;
				frame.OrdersEnded = false;
				break;
			case ScriptSequenceDefinition script:
				frame.EntrySource = RequireScriptCompiler()
					.CompileSequence(script).Create(context);
				frame.Order = startOrder;
				frame.StartRowOverride = startRow;
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
		if (sequence.EntrySource is null || sequence.OrdersEnded)
			return null;
		SequenceEntry? entry = sequence.EntrySource.GetSequenceEntry(
			checked(sequence.AbsoluteIndex++),
			sequence.Order, sequence.PreviousSequenceIndex);
		sequence.PreviousSequenceIndex = sequence.Order;
		if (entry is null)
		{
			sequence.OrdersEnded = true;
			return null;
		}
		sequence.ActiveEntry = entry;
		int startRow = sequence.StartRowOverride ?? entry.StartRow;
		sequence.StartRowOverride = null;
		if (!_resolver.TryResolve(entry.PatternId, out SongObject? source)
			|| source is null)
		{
			sequence.Order++;
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
		SequenceEntry current = sequence.ActiveEntry
			?? throw new InvalidOperationException("Missing active Sequence entry.");
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
		sequence.OrdersEnded = false;
	}

	private void PrepareSequences()
	{
		foreach (Invocation sequence in _frames.Values
			.Where(f => f.IsSequence && !f.OrdersEnded)
			.OrderBy(f => f.Depth).ThenBy(f => f.Id).ToArray())
		{
			Invocation? active = sequence.Children
				.Select(id => _frames[id])
				.FirstOrDefault(f => f.PatternId is long pid
					&& _timeline.HasUnfinishedRows(pid));
			if (active is not null)
				continue;

			if (sequence.OrderFrameId is long orderFrame
				&& sequence.Children.Contains(orderFrame))
			{
				CompleteOrder(sequence);
				sequence.OrderFrameId = null;
			}

			if (!sequence.OrdersEnded)
				sequence.OrderFrameId = EnterNextOrder(sequence);
		}
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
			PrepareSequences();
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
							if (command is SetNoteVolumeCommand noteVolume
								&& emit.Note.Target.Kind == ChannelTargetKind.Physical)
								frame.Context.GetPhysicalChannelState(
									emit.Note.Target.PhysicalChannel -
									frame.Context.PhysicalChannelBase).NoteVolume = noteVolume.Volume;
							if (command is not StartNoteCommand start || start.Mixdown
								|| !_resolver.TryResolve(start.SourceId, out SongObject? source)
								|| source is not (PatternDefinition or SequenceDefinition))
							{
								if (command is StartNoteCommand regularStart)
								{
									// An invocation's initial pitch transposes the
									// actual child notes, not their musical deadlines.
									// Flattened starts below pass the *local* factor
									// into the new child context exactly once.
									double composedPitch = regularStart.PitchMultiplier
										* frame.Context.PitchMultiplier;
									double composedGain = regularStart.GainMultiplier
										* frame.Context.GainMultiplier;
									if (composedGain < 0.0 || !double.IsFinite(composedGain))
										throw new InvalidOperationException(
											"Recursive gain multiplier is negative or non-finite.");
									if (!(composedPitch > 0.0)
										|| !double.IsFinite(composedPitch))
										throw new InvalidOperationException(
											"Recursive pitch multiplier is not positive and finite.");
									if (regularStart.Volume.HasValue
										&& emit.Note.Target.Kind == ChannelTargetKind.Physical)
										frame.Context.GetPhysicalChannelState(
											emit.Note.Target.PhysicalChannel -
											frame.Context.PhysicalChannelBase).NoteVolume =
												regularStart.Volume.Value;
									retained.Add(regularStart with
									{
										PitchMultiplier = composedPitch,
										GainMultiplier = composedGain,
										ParentOverallChannels =
											frame.Context.ParentOverallChannels.Count == 0
												? null
												: frame.Context.ParentOverallChannels,
									});
								}
								else
									retained.Add(command);
								continue;
							}
							if (emit.Note.Target.Kind != ChannelTargetKind.Physical)
								throw new NotSupportedException(
									"Flattened child requires a physical parent channel.");
							// Starting a flattened source is a *real start*
							// for the caller's remembered volume, but does not
							// create or control one individual playback voice.
							// The child's logical channel memory is fresh and
							// never absorbs the caller's host memory.
							int localChannel = emit.Note.Target.PhysicalChannel -
								frame.Context.PhysicalChannelBase;
							double noteVolume = start.Volume
								?? ReadRememberedNoteVolume?.Invoke(
									frame.Context.PhysicalPlaybackOwner,
									emit.Note.Target.PhysicalChannel, emit.Time)
								?? frame.Context.GetPhysicalChannelState(
									localChannel).NoteVolume;
							if (noteVolume < 0 || !double.IsFinite(noteVolume))
								throw new InvalidOperationException(
									"Recalled flattened source volume is invalid.");
							if (start.Volume.HasValue)
							{
								frame.Context.GetPhysicalChannelState(localChannel)
									.NoteVolume = start.Volume.Value;
								retained.Add(new RememberFlatteningNoteVolumeCommand(
									start.Volume.Value));
							}
							double localGain = start.GainMultiplier * noteVolume;
							if (localGain < 0.0 || !double.IsFinite(localGain))
								throw new InvalidOperationException(
									"Flattened source gain is negative or non-finite.");

							int physicalOffset = emit.Note.Target.PhysicalChannel
								- frame.Context.PhysicalChannelBase;
							if (physicalOffset < 0)
								throw new InvalidOperationException(
									"Child channels cannot precede parent channel space.");
							SequencingContext child = frame.Context.FlattenedChild(
								pitchMultiplier: start.PitchMultiplier,
								playbackSpeedMultiplier: start.PlaybackSpeedMultiplier,
								physicalChannelOffset: physicalOffset,
								gainMultiplier: localGain,
								physicalPlaybackOwner: _nextPlaybackOwner++);
							child.TimelineOrigin = emit.Time;
							AddInvocation(source, child, frame.Id);
						}
						if (retained.Count == 0)
							continue;
						result = emit with
						{
							Note = emit.Note with
							{
								Commands = retained.ToArray(),
								PhysicalPlaybackOwner = frame.Context.PhysicalPlaybackOwner,
							},
						};
						return true;
					default:
						result = step;
						return true;
				}
			}
			PrepareSequences();
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
		_frames.Clear();
		_patternOwners.Clear();
	}
}
