using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Stateless executable sound definition. All mutable invocation state lives in
/// a SoundState supplied by the caller.
/// </summary>
public interface ISound
{
	SoundState CreateState();

	/// <summary>
	/// Returns the first output frame which is outside the sound, relative to
	/// invocation frame zero. Null means the sound has no deterministic finite
	/// end under the supplied state.
	/// </summary>
	long? GetEndFrameExclusive(RenderContext context, SoundState state);

	/// <summary>
	/// Accumulates interleaved float PCM into destination. startFrame is
	/// invocation-relative and destination must contain exactly
	/// frameCount * output-channel-count samples.
	/// </summary>
	void Render(
		RenderContext context,
		SoundState state,
		long startFrame,
		int frameCount,
		Span<float> destination);
}
