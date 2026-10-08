using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Incremental source contract for a Pattern before tracker-time resolution.
/// Enumeration is invocation-local; callers retain the enumerator to suspend
/// and resume generation, and must dispose it when that invocation ends.
/// Progress steps let silent Patterns cooperate without synthesizing notes.
/// Note events at earlier musical-row positions than the last accepted
/// progress/event are silently discarded by consumers; equal row positions
/// retain emission order. CPU cooperation is not musical progress, and
/// fixed wall offsets do not determine raw source ordering.
/// </summary>
public interface IIncrementalRawPatternNoteGenerator
{
	IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context);
}
