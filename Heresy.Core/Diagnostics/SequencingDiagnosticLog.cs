using System;
using System.Collections.Generic;

namespace Heresy.Core.Diagnostics;

/// <summary>
/// A recoverable problem encountered while evaluating a song, not a script
/// compiler diagnostic. Reports are queued for reading outside playback.
/// </summary>
public sealed record SequencingDiagnostic(
	string Code, string Message, double? Row = null,
	double? LastAcceptedRow = null);

/// <summary>
/// Bounded diagnostic queue for a sequencing context and its descendants.
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

	private readonly Queue<SequencingDiagnostic> _pending = new();
	private long _droppedOutOfOrderNotes;

	public long DroppedOutOfOrderNotes => _droppedOutOfOrderNotes;

	public long SuppressedWarnings =>
		Math.Max(0, _droppedOutOfOrderNotes - MaximumIndividualMessages);

	/// <summary>Record one discarded note without affecting playback.</summary>
	public void ReportDroppedOutOfOrderNote(double row, double lastAcceptedRow)
	{
		if (_droppedOutOfOrderNotes < long.MaxValue)
			_droppedOutOfOrderNotes++;

		if (_droppedOutOfOrderNotes <= MaximumIndividualMessages)
		{
			_pending.Enqueue(new SequencingDiagnostic(
				OutOfOrderNoteCode,
				$"Dropped out-of-order Pattern note at row {row}; "
					+ $"last accepted musical row is {lastAcceptedRow}.",
				row, lastAcceptedRow));
		}
		else if (_droppedOutOfOrderNotes == MaximumIndividualMessages + 1)
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
		SequencingDiagnostic[] messages = _pending.ToArray();
		_pending.Clear();
		return messages;
	}
}
