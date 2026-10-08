namespace Heresy.Core.Sequencing;

/// <summary>
/// First stage of pattern execution. A data grid and a user script are two
/// interchangeable ways to produce the same raw event representation. The
/// common pattern processor subsequently resolves row timing and tracker state.
/// Notes have a chronological musical-row contract: an emission before the
/// last accepted row is silently dropped, without rewinding or reordering.
/// Equal positions retain emission order. Fixed wall offsets do not change
/// this musical-position comparison.
/// </summary>
public interface IRawPatternNoteGenerator
{
	void GenerateRawNotes(
		SequencingContext context,
		INoteReceiver output,
		out double rowCount);
}
