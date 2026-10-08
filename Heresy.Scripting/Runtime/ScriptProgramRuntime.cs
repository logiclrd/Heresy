using System;
using System.Collections.Generic;
using System.Diagnostics;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
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
public abstract class SequenceScriptProgram
{
	private readonly SequencingContext _context;
	private readonly ScriptExecutionBudget _budget = new();
	private readonly List<SequenceEntry> _entries = [];

	protected SequenceScriptProgram(SequencingContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		_context = context;
	}

	internal IReadOnlyList<SequenceEntry> Execute()
	{
		ExecuteScript();
		return _entries.ToArray();
	}

	protected abstract void ExecuteScript();

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

	protected void Play(ObjectId patternId, int startRow = 0)
	{
		if (patternId.IsNone)
			throw new ArgumentOutOfRangeException(nameof(patternId));

		Checkpoint();
		_entries.Add(new SequenceEntry(patternId, startRow));
	}
}
