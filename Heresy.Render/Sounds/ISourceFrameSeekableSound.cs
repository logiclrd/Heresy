namespace Heresy.Render.Sounds;

/// <summary>
/// Advisory cost classification for seeking a sound by native source frame.
/// This affects diagnostics/UI only; all values represent supported semantics.
/// </summary>
public enum SourceFrameSeekCost
{
	/// <summary>The sound can address the requested source frame directly.</summary>
	Direct = 0,

	/// <summary>
	/// Correct seeking may require replaying/rendering earlier source state.
	/// Realtime playback can therefore be more expensive, but export must remain
	/// semantically exact.
	/// </summary>
	ReplayRequired,
}

/// <summary>
/// Optional capability for sounds whose underlying source can be addressed in
/// native source frames. Tracker sample-offset effects use this rather than
/// converting the offset through wall-clock time.
/// </summary>
public interface ISourceFrameSeekableSound
{
	/// <summary>
	/// Advisory implementation cost. Playback correctness must never depend on
	/// this value.
	/// </summary>
	SourceFrameSeekCost SeekCost { get; }

	void SetSourceFrameOffset(
		SoundState state,
		long sourceFrameOffset);
}
