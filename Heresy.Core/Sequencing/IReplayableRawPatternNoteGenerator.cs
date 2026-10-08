using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// An incremental raw Pattern source whose raw enumeration may be restarted
/// from the beginning to revisit earlier source rows without executing
/// shared sequencing effects during enumeration. The timeline guarantees
/// ResolvePatternSourcesAtRowTime for each enumeration.
///
/// Scripts with mutable invocation state must not implement this marker
/// without explicitly providing replay-safe semantics.
/// </summary>
public interface IReplayableRawPatternNoteGenerator : IIncrementalRawPatternNoteGenerator
{
}
