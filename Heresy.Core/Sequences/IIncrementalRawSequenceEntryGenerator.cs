using System.Collections.Generic;

using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// Produces Sequence Play operations on demand. Each enumeration is one
/// independently suspended invocation; consumers must dispose unfinished
/// enumerators. CPU cooperation is distinct from musical order progress.
/// </summary>
public interface IIncrementalRawSequenceEntryGenerator
{
	IEnumerable<RawSequenceStep> EnumerateRawSteps(SequencingContext context);
}
