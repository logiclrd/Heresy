using Heresy.Render.Sounds;

namespace Heresy.Render.Sounds;

/// <summary>
/// Optional capability for sounds whose underlying source can be addressed in
/// native source frames. Tracker sample-offset effects use this rather than
/// converting the offset through wall-clock time.
/// </summary>
public interface ISourceFrameSeekableSound
{
	void SetSourceFrameOffset(
		SoundState state,
		long sourceFrameOffset);
}
