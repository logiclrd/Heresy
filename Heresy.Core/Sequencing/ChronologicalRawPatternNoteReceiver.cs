using System;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Enforces the chronological Pattern input contract at an eager raw producer
/// boundary. Notes emitted before the last accepted musical row are silently
/// ignored; notes at the same row remain in their original emission order.
/// Fixed wall-time offsets are deadlines resolved later and do not affect
/// the producer's musical-row ordering.
/// </summary>
public sealed class ChronologicalRawPatternNoteReceiver : INoteReceiver
{
	private readonly INoteReceiver _output;
	private double _lastRow;

	public ChronologicalRawPatternNoteReceiver(INoteReceiver output)
		=> _output = output ?? throw new ArgumentNullException(nameof(output));

	public void Append(NoteEvent noteEvent)
	{
		ArgumentNullException.ThrowIfNull(noteEvent);
		double row = noteEvent.Offset.RowOffset;
		if (row < _lastRow)
			return;
		_lastRow = row;
		_output.Append(noteEvent);
	}
}
