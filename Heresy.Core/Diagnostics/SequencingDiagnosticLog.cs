using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

namespace Heresy.Core.Diagnostics;

/// <summary>
/// A recoverable problem encountered while evaluating a song, not a script
/// compiler diagnostic. Reports are queued for reading outside playback.
/// </summary>
public sealed record SequencingDiagnostic(
	string Code, string Message, double? Row = null,
	double? LastAcceptedRow = null);

/// <summary>
/// Thread-safe bounded diagnostic queue for a sequencing context and its descendants.
/// At most 32 individual dropped-note warnings and one suppression notice
/// are enqueued per session, regardless of how often callers drain them.
/// After the cap is reached only counters advance; no additional messages
/// are allocated. No application/UI callbacks run on the audio thread.
/// </summary>
public sealed class SequencingDiagnosticLog
{
	public const int MaximumIndividualMessages = 32;
	public const string OutOfOrderNoteCode = "HRSEQ001";
	public const string SuppressionCode = "HRSEQ002";
	public const string IgnoredFlatteningEffectCode = "HRSEQ003";
	public const string IgnoredFlatteningSuppressionCode = "HRSEQ004";
	public const string ExpensiveSourceSeekCode = "HRSEQ005";
	public const string ExpensiveSourceSeekSuppressionCode = "HRSEQ006";
	public const string NoteFilterCommandCode = "HRSEQ007";
	public const string NoteFilterCommandSuppressionCode = "HRSEQ008";
	public const string ChannelFilterCommandCode = "HRSEQ009";
	public const string ChannelFilterCommandSuppressionCode = "HRSEQ010";

	private readonly ConcurrentQueue<SequencingDiagnostic> _pending = new();
	private long _droppedOutOfOrderNotes;
	private long _ignoredFlatteningEffects;
	private long _expensiveSourceSeeks;
	private long _rejectedNoteFilterCommands;
	private long _rejectedChannelFilterCommands;

	public long RejectedChannelFilterCommands =>
		Interlocked.Read(ref _rejectedChannelFilterCommands);

	public void ReportChannelFilterCommandIssue(string reason)
	{
		long count = Interlocked.Increment(ref _rejectedChannelFilterCommands);
		if (count <= MaximumIndividualMessages)
			_pending.Enqueue(new SequencingDiagnostic(
				ChannelFilterCommandCode,
				$"Ignored channel Filter command: {reason}"));
		else if (count == MaximumIndividualMessages + 1)
			_pending.Enqueue(new SequencingDiagnostic(
				ChannelFilterCommandSuppressionCode,
				"Further invalid channel Filter command diagnostics are suppressed."));
	}

	public long RejectedNoteFilterCommands =>
		Interlocked.Read(ref _rejectedNoteFilterCommands);

	/// <summary>Control-side advisory for an absent note/Filter, invalid
	/// stage assignment, or unavailable Filter ID. Never mutate audio state
	/// or invoke UI callbacks while reporting it.</summary>
	public void ReportNoteFilterCommandIssue(string reason)
	{
		long count = Interlocked.Increment(ref _rejectedNoteFilterCommands);
		if (count <= MaximumIndividualMessages)
			_pending.Enqueue(new SequencingDiagnostic(
				NoteFilterCommandCode, $"Ignored note Filter command: {reason}"));
		else if (count == MaximumIndividualMessages + 1)
			_pending.Enqueue(new SequencingDiagnostic(
				NoteFilterCommandSuppressionCode,
				"Further invalid note Filter command diagnostics are suppressed."));
	}

	public long ExpensiveSourceSeeks => Interlocked.Read(ref _expensiveSourceSeeks);

	/// <summary>Advisory diagnostic for an actual ReplayRequired seek or
	/// retrigger, emitted only after the renderer has resolved the bound
	/// sound's capability. It never changes musical semantics.</summary>
	public void ReportExpensiveSourceSeek(string effect, long nativeFrame)
	{
		long count = Interlocked.Increment(ref _expensiveSourceSeeks);
		if (count <= MaximumIndividualMessages)
			_pending.Enqueue(new SequencingDiagnostic(
				ExpensiveSourceSeekCode,
				$"{effect} on a ReplayRequired source (native frame {nativeFrame}) may replay earlier PCM in realtime; offline export remains accurate."));
		else if (count == MaximumIndividualMessages + 1)
			_pending.Enqueue(new SequencingDiagnostic(
				ExpensiveSourceSeekSuppressionCode,
				"Further expensive native-source-seek hints are suppressed."));
	}


	public long IgnoredFlatteningEffects =>
		Interlocked.Read(ref _ignoredFlatteningEffects);

	public void ReportIgnoredFlatteningEffect(string effect, double row)
	{
		long count = Interlocked.Increment(ref _ignoredFlatteningEffects);
		if (count <= MaximumIndividualMessages)
			_pending.Enqueue(new SequencingDiagnostic(
				IgnoredFlatteningEffectCode,
				$"Ignored incompatible voice-specific operation of {effect} on the flattened source's logical channel at row {row}; applicable source-volume operations remain active.",
				row));
		else if (count == MaximumIndividualMessages + 1)
			_pending.Enqueue(new SequencingDiagnostic(
				IgnoredFlatteningSuppressionCode,
				"Further flattened-source effect warnings are suppressed."));
	}

	public long DroppedOutOfOrderNotes => Interlocked.Read(ref _droppedOutOfOrderNotes);

	public long SuppressedWarnings =>
		Math.Max(0, DroppedOutOfOrderNotes - MaximumIndividualMessages);

	/// <summary>Record one discarded note without affecting playback.</summary>
	public void ReportDroppedOutOfOrderNote(double row, double lastAcceptedRow)
	{
		long count = Interlocked.Increment(ref _droppedOutOfOrderNotes);

		if (count <= MaximumIndividualMessages)
		{
			_pending.Enqueue(new SequencingDiagnostic(
				OutOfOrderNoteCode,
				$"Dropped out-of-order Pattern note at row {row}; "
					+ $"last accepted musical row is {lastAcceptedRow}.",
				row, lastAcceptedRow));
		}
		else if (count == MaximumIndividualMessages + 1)
		{
			_pending.Enqueue(new SequencingDiagnostic(
				SuppressionCode,
				"Further out-of-order Pattern note warnings are suppressed "
					+ "for this sequencing context."));
		}
	}

	/// <summary>Consume currently queued reports without resetting the cap.</summary>
	public SequencingDiagnostic[] Drain()
	{
		List<SequencingDiagnostic> messages = [];
		while (_pending.TryDequeue(out SequencingDiagnostic? message))
			messages.Add(message);
		return messages.ToArray();
	}
}
