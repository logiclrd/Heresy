using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Sequences;
using Heresy.Core.Timing;

namespace Heresy.Scripting.Runtime;

internal sealed class ScriptExecutionBudget
{
	private const int StopwatchCheckMask = 0x3FF;
	private static readonly TimeSpan MaximumDuration =
		TimeSpan.FromSeconds(10);

	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
	private int _units;

	public void Checkpoint()
	{
		_units++;
		if (_units > NoteScheduleBuilder.MaximumGeneratedNotes)
		{
			throw new SequencingResourceLimitException(
				$"Script execution exceeded {NoteScheduleBuilder.MaximumGeneratedNotes:N0} expansion units.");
		}

		if ((_units & StopwatchCheckMask) == 0
			&& _stopwatch.Elapsed > MaximumDuration)
		{
			throw new SequencingResourceLimitException(
				"Script execution exceeded the 10 second runaway budget.");
		}
	}
}

/// <summary>
/// Single pending raw event for a resumable script. The generated iterator
/// suspends immediately after each helper call instead of constructing an
/// eager note schedule.
/// </summary>
internal sealed class IncrementalPatternEventReceiver : INoteReceiver
{
	private NoteEvent? _pending;
	internal bool HasPending => _pending is not null;

	public void Append(NoteEvent noteEvent)
	{
		ArgumentNullException.ThrowIfNull(noteEvent);
		if (_pending is not null)
			throw new InvalidOperationException(
				"An incremental script must yield after every note helper.");
		_pending = noteEvent;
	}

	public NoteEvent Take()
	{
		NoteEvent eventToYield = _pending
			?? throw new InvalidOperationException("No raw script event is pending.");
		_pending = null;
		return eventToYield;
	}
}

/// <summary>
/// Per-invocation runtime surface inherited by compiled pattern scripts.
/// Instances are never reused between sequencing invocations.
/// </summary>
public abstract class PatternScriptProgram
{
	private readonly SequencingContext _context;
	private readonly INoteReceiver _output;
	private readonly double _rowCount;
	private readonly int _channelCount;
	private readonly ScriptExecutionBudget _budget = new();
	private int _cooperationIterations;
	private double _lastRawRow;
	private bool _collectingRelaxed;
	private SequenceCallsite? _editorActionCallsite;
	private VirtualChannelLeaseAllocator? _virtualLeases;
	private Queue<VirtualChannelIdentity>? _releasedVirtualChannels;

	protected PatternScriptProgram(
		SequencingContext context,
		INoteReceiver output,
		double rowCount,
		int channelCount)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);
		if (double.IsNaN(rowCount)
			|| double.IsInfinity(rowCount)
			|| rowCount < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(rowCount));
		}
		if (channelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(channelCount));

		_context = context;
		_output = output;
		_rowCount = rowCount;
		_channelCount = channelCount;
	}

	// Trusted ownership runtime only. No numeric lease API is exposed to
	// authored source. The handle/group implementation will own these
	// leases; the streaming wrapper publishes releases at yield boundaries.
	internal VirtualChannelIdentity AcquireVirtualChannelLease()
		=> (_virtualLeases ??= new()).Acquire();

	internal bool ReleaseVirtualChannelLease(VirtualChannelIdentity identity)
	{
		if (_virtualLeases?.Release(identity) != true)
			return false;
		if (_output is IncrementalPatternEventReceiver)
			(_releasedVirtualChannels ??= new()).Enqueue(identity);
		return true;
	}

	internal bool TryTakeVirtualChannelRelease(out VirtualChannelIdentity identity)
	{
		if (_releasedVirtualChannels is not null)
			return _releasedVirtualChannels.TryDequeue(out identity);
		identity = default;
		return false;
	}

	internal bool OwnsVirtualChannelLease(VirtualChannelIdentity identity)
		=> _virtualLeases?.Owns(identity) == true;

	internal void ReportVirtualOwnershipIssue(string reason)
		=> _context.Diagnostics.ReportVirtualOwnershipIssue(reason);

	private void CleanupVirtualLeases()
	{
		if (_virtualLeases is { ActiveCount: > 0 } allocator)
		{
			ReportVirtualOwnershipIssue("Script Pattern ended with undisposed virtual note leases; released remaining ownership.");
			allocator.ReleaseAll();
		}
	}

	internal void Execute()
	{
		try { ExecuteScript(); }
		finally { CleanupVirtualLeases(); }
	}

	internal void ExecuteRelaxed()
	{
		_context.Cancellation.ThrowIfCancellationRequested();
		_collectingRelaxed = true;
		try
		{
			ExecuteScript();
			_context.Cancellation.ThrowIfCancellationRequested();
		}
		finally { _collectingRelaxed = false; CleanupVirtualLeases(); }
	}

	// The eager and resumable wrappers are deliberately separate. Eager
	// callers cannot accidentally enumerate a streaming program, or vice versa.
	internal IEnumerable<RawPatternStep> Enumerate()
	{
		try
		{
			foreach (RawPatternStep step in EnumerateScript())
				yield return step;
		}
		finally { CleanupVirtualLeases(); }
	}

	protected abstract void ExecuteScript();

	protected virtual IEnumerable<RawPatternStep> EnumerateScript()
		=> throw new NotSupportedException(
			"This compiled script does not support resumable execution.");

	protected RawPatternStep EmitPendingStep()
		=> _output is IncrementalPatternEventReceiver receiver
			? new RawPatternStep.Emit(receiver.Take())
			: throw new InvalidOperationException(
				"Resumable scripts need an invocation-local event receiver.");

	// Trusted strict rewrite: invalid ownership operations emit no event.
	protected bool HasPendingEvent()
		=> _output is IncrementalPatternEventReceiver { HasPending: true };

	protected bool ShouldCooperate()
	{
		// Periodic CPU cooperation is not musical progress and has no
		// lifetime iteration ceiling for a valid infinitely looping script.
		_cooperationIterations = (_cooperationIterations + 1) & 127;
		return _cooperationIterations == 0;
	}

	protected RawPatternStep CpuCheckpoint()
		=> new RawPatternStep.Cooperate(_lastRawRow);

	protected ObjectId _O(uint id)
	{
		if (id == 0)
			throw new ArgumentOutOfRangeException(nameof(id));

		return (ObjectId)id;
	}

	/// <summary>
	/// Signed computed IDs follow the same unresolved-reference path as
	/// unknown unsigned IDs. Negative values wrap to nonzero UInt32 IDs;
	/// nonnegative values delegate to the existing unsigned lookup.
	/// </summary>
	protected ObjectId _O(int id)
		=> id < 0
			? (ObjectId)unchecked((uint)id)
			: _O((uint)id);

	protected double Random()
	{
		Checkpoint();
		return _context.Random.NextDouble();
	}

	protected void Checkpoint()
	{
		if (_collectingRelaxed)
			_context.Cancellation.ThrowIfCancellationRequested();
		_budget.Checkpoint();
	}

	protected void Note(
		double row,
		int channel,
		ObjectId sourceId,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		bool mixdown = false,
		double? volume = null,
		double timeOffsetSeconds = 0.0,
		ObjectId? eventFilterDefinitionId = null)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		AppendNoteStart(row, ChannelTarget.Physical(channel),
			sourceId, pitchMultiplier, playbackSpeedMultiplier,
			mixdown, volume, timeOffsetSeconds, eventFilterDefinitionId);
	}

	/// <summary>Allocate an exclusive logical channel and schedule its first note.</summary>
	protected VirtualNote NoteVirtual(double row, ObjectId sourceId,
		double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0,
		bool mixdown = false, double? volume = null,
		double timeOffsetSeconds = 0.0, ObjectId? eventFilterDefinitionId = null)
	{
		ValidateRow(row);
		VirtualChannelIdentity identity = AcquireVirtualChannelLease();
		try
		{
			AppendNoteStart(row, ChannelTarget.Virtual(identity), sourceId,
				pitchMultiplier, playbackSpeedMultiplier, mixdown, volume,
				timeOffsetSeconds, eventFilterDefinitionId);
			return new VirtualNote(this, identity);
		}
		catch
		{
			ReleaseVirtualChannelLease(identity);
			throw;
		}
	}

	protected VirtualNote NoteVirtual(SequenceCallsite callsite, double row,
		ObjectId sourceId, double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0, bool mixdown = false,
		double? volume = null, double timeOffsetSeconds = 0.0,
		ObjectId? eventFilterDefinitionId = null)
	{
		SequenceCallsite? previous = _editorActionCallsite;
		_editorActionCallsite = previous ?? callsite;
		try { return NoteVirtual(row, sourceId, pitchMultiplier,
			playbackSpeedMultiplier, mixdown, volume, timeOffsetSeconds, eventFilterDefinitionId); }
		finally { _editorActionCallsite = previous; }
	}

	internal void StartVirtualLease(VirtualChannelIdentity identity, double row,
		ObjectId sourceId, double pitchMultiplier, double playbackSpeedMultiplier,
		bool mixdown, double? volume, double timeOffsetSeconds,
		ObjectId? eventFilterDefinitionId, SequenceCallsite? callsite)
	{
		ValidateRow(row);
		SequenceCallsite? previous = _editorActionCallsite;
		_editorActionCallsite = previous ?? callsite;
		try { AppendNoteStart(row, ChannelTarget.Virtual(identity), sourceId,
			pitchMultiplier, playbackSpeedMultiplier, mixdown, volume,
			timeOffsetSeconds, eventFilterDefinitionId); }
		finally { _editorActionCallsite = previous; }
	}

	internal void ControlVirtualLease(VirtualChannelIdentity identity, double row,
		NoteCommand command, double timeOffsetSeconds, SequenceCallsite? callsite)
	{
		ValidateRow(row);
		SequenceCallsite? previous = _editorActionCallsite;
		_editorActionCallsite = previous ?? callsite;
		try { Append(row, ChannelTarget.Virtual(identity), command, timeOffsetSeconds); }
		finally { _editorActionCallsite = previous; }
	}

	/// <summary>Only rewritten editor-F6 calls use these overloads. Normal
	/// Pattern scripts continue to call the unchanged musical API.</summary>
	private void EmitWithEditorCallsite(SequenceCallsite callsite, Action emit)
	{
		SequenceCallsite? previous = _editorActionCallsite;
		// An enclosing authored delegate invocation takes precedence over
		// the direct emitter (or method-group fallback) inside its body.
		_editorActionCallsite = previous ?? callsite;
		try { emit(); }
		finally { _editorActionCallsite = previous; }
	}

	// Generated F6 source only. The canonical authored-member allow-list
	// rejects all three helpers, even when relaxed ordering is enabled.
	// Preserve the exact delegate type at the original invocation boundary;
	// the adapter body executes only after receiver and arguments are evaluated.
	protected TDelegate __HeresyWrapDelegate<TDelegate>(
		TDelegate target, Func<TDelegate, TDelegate> createAdapter)
		where TDelegate : Delegate
		=> createAdapter(target);

	protected SequenceCallsite? __HeresyPushDelegateCallsite(SequenceCallsite callsite)
	{
		SequenceCallsite? previous = _editorActionCallsite;
		_editorActionCallsite = callsite;
		return previous;
	}

	protected void __HeresyRestoreDelegateCallsite(SequenceCallsite? previous)
		=> _editorActionCallsite = previous;

	protected void Note(SequenceCallsite callsite, double row, int channel, ObjectId sourceId,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		bool mixdown = false, double? volume = null,
		double timeOffsetSeconds = 0.0,
		ObjectId? eventFilterDefinitionId = null)
		=> EmitWithEditorCallsite(callsite,
			() => Note(row, channel, sourceId, pitchMultiplier, playbackSpeedMultiplier, mixdown, volume, timeOffsetSeconds, eventFilterDefinitionId));

	protected void NoteFilter(SequenceCallsite callsite, double row, int channel, ObjectId? definitionId = null,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => NoteFilter(row, channel, definitionId, timeOffsetSeconds));

	protected void ChannelFilter(SequenceCallsite callsite, double row, int channel, ObjectId? definitionId = null,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => ChannelFilter(row, channel, definitionId, timeOffsetSeconds));

	// Editor-F6-only provenance for voice-ending operations. Delegate to
	// the authored overloads so validation, channel scoping and note
	// commands remain identical to ordinary playback.
	protected void Off(SequenceCallsite callsite, double row, int channel,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Off(row, channel, timeOffsetSeconds));

	protected void Cut(SequenceCallsite callsite, double row, int channel,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Cut(row, channel, timeOffsetSeconds));

	protected void OffVirtualInScope(SequenceCallsite callsite, double row,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => OffVirtualInScope(row, timeOffsetSeconds));

	protected void CutVirtualInScope(SequenceCallsite callsite, double row,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => CutVirtualInScope(row, timeOffsetSeconds));

	protected void OffAllVirtual(SequenceCallsite callsite, double row,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => OffAllVirtual(row, timeOffsetSeconds));

	protected void CutAllVirtual(SequenceCallsite callsite, double row,
		double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => CutAllVirtual(row, timeOffsetSeconds));

	// Editor-F6-only provenance for global timing and physical-channel
	// playback controls. All actual parameter validation and emission
	// remain in the existing authored overloads.
	protected void Tempo(SequenceCallsite callsite, double row, double ticksPerDiachron)
		=> EmitWithEditorCallsite(callsite,
			() => Tempo(row, ticksPerDiachron));

	protected void Speed(SequenceCallsite callsite, double row, int ticksPerRow)
		=> EmitWithEditorCallsite(callsite,
			() => Speed(row, ticksPerRow));

	protected void Seek(SequenceCallsite callsite, double row, int channel,
		double playbackSeconds, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Seek(row, channel, playbackSeconds, timeOffsetSeconds));

	protected void Pan(SequenceCallsite callsite, double row, int channel,
		double x, double y = 0, double z = 0, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Pan(row, channel, x, y, z, timeOffsetSeconds));

	protected void Surround(SequenceCallsite callsite, double row, int channel,
		bool enabled, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Surround(row, channel, enabled, timeOffsetSeconds));

	protected void Filter(SequenceCallsite callsite, double row, int channel,
		double cutoff, double resonance, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => Filter(row, channel, cutoff, resonance, timeOffsetSeconds));

	protected void FilterCutoff(SequenceCallsite callsite, double row, int channel,
		double cutoff, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => FilterCutoff(row, channel, cutoff, timeOffsetSeconds));

	protected void FilterResonance(SequenceCallsite callsite, double row,
		int channel, double resonance, double timeOffsetSeconds = 0.0)
		=> EmitWithEditorCallsite(callsite,
			() => FilterResonance(row, channel, resonance, timeOffsetSeconds));

	// Editor-F6-only provenance for virtual-channel control commands.
	// The authored overloads still own validation and command emission.
	private void AppendNoteStart(
		double row,
		ChannelTarget target,
		ObjectId sourceId,
		double pitchMultiplier,
		double playbackSpeedMultiplier,
		bool mixdown,
		double? volume,
		double timeOffsetSeconds,
		ObjectId? eventFilterDefinitionId)
	{
		if (eventFilterDefinitionId is { IsNone: true })
			throw new ArgumentOutOfRangeException(nameof(eventFilterDefinitionId));
		if (sourceId.IsNone)
			throw new ArgumentOutOfRangeException(nameof(sourceId));
		ValidatePositiveFinite(pitchMultiplier, nameof(pitchMultiplier));
		ValidatePositiveFinite(playbackSpeedMultiplier,
			nameof(playbackSpeedMultiplier));
		if (volume.HasValue
			&& (!double.IsFinite(volume.Value)
				|| volume.Value < 0.0
				|| volume.Value > 1.0))
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}

		Append(row, target,
			new StartNoteCommand(sourceId, pitchMultiplier,
				playbackSpeedMultiplier, mixdown, volume)
			{
				EventFilterDefinitionId = eventFilterDefinitionId,
			},
			timeOffsetSeconds);
	}

	/// <summary>Select or clear the current physical note's outer Filter.
	/// This is not the persistent IT channel filter, nor an Instrument tone
	/// Filter. The definition ID is resolved by the prepared playback factory.</summary>
	protected void NoteFilter(
		double row, int channel, ObjectId? definitionId = null,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		Append(row, ChannelTarget.Physical(channel),
			new SetNoteFilterCommand(definitionId), timeOffsetSeconds);
	}

	/// <summary>Choose or clear the persistent output Filter on the actual
	/// physical host, independent of note events and invocation memory.</summary>
	protected void ChannelFilter(double row, int channel,
		ObjectId? definitionId = null, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		Append(row, ChannelTarget.Physical(channel),
			new SetChannelFilterCommand(definitionId), timeOffsetSeconds);
	}

	/// <summary>Release previously started virtual voices in this scope.</summary>
	protected void OffVirtualInScope(
		double row, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		Append(row, ChannelTarget.AllVirtualInScope,
			new NoteOffCommand(), timeOffsetSeconds);
	}

	/// <summary>Cut previously started virtual voices in this scope.</summary>
	protected void CutVirtualInScope(
		double row, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		Append(row, ChannelTarget.AllVirtualInScope,
			new NoteCutCommand(), timeOffsetSeconds);
	}

	/// <summary>Release previously started virtual voices in every scope.</summary>
	protected void OffAllVirtual(
		double row, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		Append(row, ChannelTarget.AllVirtual,
			new NoteOffCommand(), timeOffsetSeconds);
	}

	/// <summary>Cut previously started virtual voices in every scope.</summary>
	protected void CutAllVirtual(
		double row, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		Append(row, ChannelTarget.AllVirtual,
			new NoteCutCommand(), timeOffsetSeconds);
	}

	protected void Off(double row, int channel, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		Append(
			row,
			ChannelTarget.Physical(channel),
			new NoteOffCommand(), timeOffsetSeconds);
	}

	protected void Cut(double row, int channel, double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		Append(
			row,
			ChannelTarget.Physical(channel),
			new NoteCutCommand(), timeOffsetSeconds);
	}

	protected void Tempo(double row, double ticksPerDiachron)
	{
		ValidateRow(row);
		ValidatePositiveFinite(
			ticksPerDiachron,
			nameof(ticksPerDiachron));
		Append(
			row,
			ChannelTarget.Global,
			new SetTempoCommand(ticksPerDiachron));
	}

	protected void Speed(double row, int ticksPerRow)
	{
		ValidateRow(row);
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));

		Append(
			row,
			ChannelTarget.Global,
			new SetSpeedCommand(ticksPerRow));
	}

	/// <summary>Move the existing physical-channel voice to an explicit
	/// source playback-time offset. Unlike tracker Oxx, this is a wall-time
	/// offset and follows the renderer's established seek semantics.</summary>
	protected void Seek(
		double row, int channel, double playbackSeconds,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		if (!double.IsFinite(playbackSeconds)
			|| playbackSeconds < 0
			|| playbackSeconds > TimeSpan.MaxValue.TotalSeconds)
			throw new ArgumentOutOfRangeException(nameof(playbackSeconds));
		Append(row, ChannelTarget.Physical(channel),
			new SetPlaybackOffsetCommand(TimeSpan.FromSeconds(playbackSeconds)),
			timeOffsetSeconds);
	}

	/// <summary>Set physical-channel spatial position. These are absolute
	/// normalized scene coordinates, not IT's 0..64 panning column.</summary>
	protected void Pan(
		double row, int channel, double x, double y = 0, double z = 0,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		if (!double.IsFinite(x) || !double.IsFinite(y)
			|| !double.IsFinite(z)
			|| Math.Abs(x) > float.MaxValue
			|| Math.Abs(y) > float.MaxValue
			|| Math.Abs(z) > float.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(x));
		Append(row, ChannelTarget.Physical(channel),
			new SetSpatialPositionCommand(
				new Vector3((float)x, (float)y, (float)z)),
			timeOffsetSeconds);
	}

	protected void Surround(
		double row, int channel, bool enabled,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		Append(row, ChannelTarget.Physical(channel),
			new SetSurroundCommand(enabled), timeOffsetSeconds);
	}

	/// <summary>IT-style filter values normalized to the interval [0,1].
	/// The physical channel retains these values for subsequent voices.</summary>
	protected void Filter(
		double row, int channel, double cutoff, double resonance,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		ValidateUnitInterval(cutoff, nameof(cutoff));
		ValidateUnitInterval(resonance, nameof(resonance));
		Append(row, ChannelTarget.Physical(channel),
			new SetResonantFilterCommand(cutoff, resonance),
			timeOffsetSeconds);
	}

	protected void FilterCutoff(
		double row, int channel, double cutoff,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		ValidateUnitInterval(cutoff, nameof(cutoff));
		Append(row, ChannelTarget.Physical(channel),
			new SetResonantFilterCutoffCommand(cutoff), timeOffsetSeconds);
	}

	protected void FilterResonance(
		double row, int channel, double resonance,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		ValidateUnitInterval(resonance, nameof(resonance));
		Append(row, ChannelTarget.Physical(channel),
			new SetResonantFilterResonanceCommand(resonance),
			timeOffsetSeconds);
	}

	private static void ValidateUnitInterval(double value, string name)
	{
		if (!double.IsFinite(value) || value < 0 || value > 1)
			throw new ArgumentOutOfRangeException(name);
	}

	private void Append(
		double row,
		ChannelTarget target,
		NoteCommand command,
		double timeOffsetSeconds = 0.0)
	{
		if (!double.IsFinite(timeOffsetSeconds)
			|| Math.Abs(timeOffsetSeconds) > TimeSpan.MaxValue.TotalSeconds)
			throw new ArgumentOutOfRangeException(nameof(timeOffsetSeconds));
		Checkpoint();
		if (command is StartNoteCommand start
			&& _editorActionCallsite is { } origin)
			command = start with { EditorOriginCallsite = origin };
		_output.Append(
			new NoteEvent(
				new MusicalTime(TimeSpan.FromSeconds(timeOffsetSeconds), row),
				target,
				[command])
			{
				EditorCallsite = _editorActionCallsite,
				EditorPatternRow = _editorActionCallsite.HasValue
					? (int)Math.Floor(row) : null,
			});
		_lastRawRow = row;
	}

	private void ValidateRow(double row)
	{
		if (double.IsNaN(row)
			|| double.IsInfinity(row)
			|| row < 0.0
			|| row > _rowCount)
		{
			throw new ArgumentOutOfRangeException(nameof(row));
		}
	}

	private void ValidateChannel(int channel)
	{
		if ((uint)channel >= (uint)_channelCount)
			throw new ArgumentOutOfRangeException(nameof(channel));
	}

	private static void ValidatePositiveFinite(
		double value,
		string paramName)
	{
		if (!(value > 0.0)
			|| double.IsNaN(value)
			|| double.IsInfinity(value))
		{
			throw new ArgumentOutOfRangeException(paramName);
		}
	}
}

/// <summary>
/// Per-invocation runtime surface inherited by compiled sequence scripts.
/// A script builds ordinary SequenceEntry values which are then consumed by
/// the common SequenceNoteProcessor.
/// </summary>
public abstract class SequenceScriptProgram : ISequenceEntryProvider
{
	private readonly SequencingContext _context;
	private ScriptExecutionBudget _budget = new();

	protected SequenceScriptProgram(SequencingContext context)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
	}

	/// <summary>
	/// Called exactly once per visited order; local script variables are
	/// fresh on each call. The per-invocation RNG remains available through
	/// Random(). The first previousSequenceIndex is -1.
	/// </summary>
	public SequenceEntry? GetSequenceEntry(
		int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
	{
		if (absoluteIndex < 0 || sequenceIndex < 0 || previousSequenceIndex < -1)
			throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
		// The execution budget bounds a single lookup, not the duration of
		// valid indefinitely repeating music.
		_budget = new ScriptExecutionBudget();
		return ExecuteEntry(absoluteIndex, sequenceIndex, previousSequenceIndex);
	}

	protected abstract SequenceEntry? ExecuteEntry(
		int absoluteIndex, int sequenceIndex, int previousSequenceIndex);

	protected ObjectId _O(uint id)
	{
		if (id == 0)
			throw new ArgumentOutOfRangeException(nameof(id));
		return (ObjectId)id;
	}

	/// <summary>
	/// Negative signed IDs are unresolved in the same way as unknown
	/// unsigned IDs; other values delegate to the established UInt32 lookup.
	/// </summary>
	protected ObjectId _O(int id)
		=> id < 0
			? (ObjectId)unchecked((uint)id)
			: _O((uint)id);

	protected double Random()
	{
		Checkpoint();
		return _context.Random.NextDouble();
	}

	protected void Checkpoint() => _budget.Checkpoint();

	/// <summary>
	/// Construct one order entry for return by the scripted lookup function.
	/// Does not launch the child or change any Sequence iteration state.
	/// </summary>
	protected SequenceEntry Play(ObjectId patternId, int startRow = 0)
	{
		if (patternId.IsNone)
			throw new ArgumentOutOfRangeException(nameof(patternId));
		Checkpoint();
		return new SequenceEntry(patternId, startRow);
	}

	/// <summary>Called only by a specially compiled editor-audition script.
	/// The leading metadata parameter cannot displace existing optional
	/// arguments. Normal Play calls never enter this method or its hook.</summary>
	protected SequenceEntry Play(SequenceCallsite callsite,
		ObjectId patternId, int startRow = 0)
	{
		SequenceEntry entry = Play(patternId, startRow)
			with { EditorCallsite = callsite };
		OnInstrumentedPlay(callsite, entry);
		return entry;
	}

	/// <summary>Optional probe for immediate call execution. The UI's basic
	/// highlight uses the returned entry's scheduled activation instead.</summary>
	protected virtual void OnInstrumentedPlay(
		SequenceCallsite callsite, SequenceEntry entry) { }
}
