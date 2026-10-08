using Heresy.Core.Timing;

namespace Heresy.Core.Sequencing;

/// <summary>
/// One incremental result from the raw Pattern producer. These are not
/// wall-time playback events: the common PatternNoteProcessor is responsible
/// for resolving their musical positions, commands and shared tracker state.
/// </summary>
public abstract record RawPatternStep(double Row)
{
	/// <summary>A raw command-bearing event in Pattern-local row coordinates.</summary>
	public sealed record Emit(NoteEvent Note) : RawPatternStep(Note.Offset.RowOffset);

	/// <summary>
	/// Cooperative progress through a silent region. No command is executed;
	/// Row is the boundary reached, not a calculated wall-time timestamp.
	/// </summary>
	public sealed record Advance(double Row) : RawPatternStep(Row);
}
