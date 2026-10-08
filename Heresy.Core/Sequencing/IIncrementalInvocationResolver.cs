using Heresy.Core.Objects;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Resolves a source ID to the immutable song-object snapshot to be used by
/// one incremental invocation. Callers should avoid returning editable
/// live objects while a background playback session is running.
/// </summary>
public interface IIncrementalInvocationResolver
{
	bool TryResolve(ObjectId id, out SongObject? source);
}
