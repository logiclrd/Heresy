using Heresy.Core.Objects;
using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Resolves a persistent song-object reference into an executable sound.
/// Returning false represents an unresolved/deleted reference and therefore
/// silence; diagnostics can be layered around the resolver later.
/// </summary>
public interface ISoundResolver
{
	bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound);
}
