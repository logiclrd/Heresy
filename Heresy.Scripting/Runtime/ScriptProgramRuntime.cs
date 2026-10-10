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

	internal void Execute() => ExecuteScript();

	// The eager and resumable wrappers are deliberately separate. Eager
	// callers cannot accidentally enumerate a streaming program, or vice versa.
	internal IEnumerable<RawPatternStep> Enumerate() => EnumerateScript();

	protected abstract void ExecuteScript();

	protected virtual IEnumerable<RawPatternStep> EnumerateScript()
		=> throw new NotSupportedException(
			"This compiled script does not support resumable execution.");

	protected RawPatternStep EmitPendingStep()
		=> _output is IncrementalPatternEventReceiver receiver
			? new RawPatternStep.Emit(receiver.Take())
			: throw new InvalidOperationException(
				"Resumable scripts need an invocation-local event receiver.");

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

	protected double Random()
	{
		Checkpoint();
		return _context.Random.NextDouble();
	}

	protected void Checkpoint() => _budget.Checkpoint();

	protected void Note(
		double row,
		int channel,
		ObjectId sourceId,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		bool mixdown = false,
		double? volume = null,
		double timeOffsetSeconds = 0.0)
	{
		ValidateRow(row);
		ValidateChannel(channel);
		if (sourceId.IsNone)
			throw new ArgumentOutOfRangeException(nameof(sourceId));
		ValidatePositiveFinite(
			pitchMultiplier,
			nameof(pitchMultiplier));
		ValidatePositiveFinite(
			playbackSpeedMultiplier,
			nameof(playbackSpeedMultiplier));
		if (volume.HasValue
			&& (double.IsNaN(volume.Value)
				|| double.IsInfinity(volume.Value)
				|| volume.Value < 0.0
				|| volume.Value > 1.0))
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}

		Append(
			row,
			ChannelTarget.Physical(channel),
			new StartNoteCommand(
				sourceId,
				pitchMultiplier,
				playbackSpeedMultiplier,
				mixdown,
				volume),
			timeOffsetSeconds);
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
		_output.Append(
			new NoteEvent(
				new MusicalTime(TimeSpan.FromSeconds(timeOffsetSeconds), row),
				target,
				[command]));
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
}
