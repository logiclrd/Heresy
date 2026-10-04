namespace Heresy.Core.Sequencing;

/// <summary>
/// First stage of pattern execution. A data grid and a user script are two
/// interchangeable ways to produce the same raw event representation. The
/// common pattern processor subsequently resolves row timing and tracker state.
/// </summary>
public interface IRawPatternNoteGenerator
{
	void GenerateRawNotes(
		SequencingContext context,
		INoteReceiver output,
		out double rowCount);
}
