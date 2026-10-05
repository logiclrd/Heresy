using Heresy.Core.Objects;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Resolves a sequence's stable pattern references into executable pattern
/// generators for the current song snapshot or compiled scripting context.
/// </summary>
public interface ISequencePatternResolver
{
	bool TryResolve(
		ObjectId patternId,
		out IRawPatternNoteGenerator? pattern);
}
