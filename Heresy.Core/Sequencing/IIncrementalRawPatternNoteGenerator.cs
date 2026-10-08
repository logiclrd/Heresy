using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Incremental source contract for a Pattern before tracker-time resolution.
/// Enumeration is invocation-local; callers retain the enumerator to suspend
/// and resume generation, and must dispose it when that invocation ends.
/// Progress steps let silent Patterns cooperate without synthesizing notes.
/// </summary>
public interface IIncrementalRawPatternNoteGenerator
{
	IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context);
}
