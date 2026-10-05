using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Executable sound definition. Mutable invocation state lives in SoundState;
/// configuration which must remain fixed for an already-started note is
/// captured by SnapshotNoteConfiguration.
/// </summary>
public interface ISound
{
	NoteConfigurationSnapshot SnapshotNoteConfiguration();

	SoundState CreateState();

	/// <summary>
	/// Binds one requested note to an executable sound/state/configuration.
	/// Ordinary sounds use the default implementation. Recursive sounds such as
	/// instruments may override this to select and bind a child sound before the
	/// PlaybackVoice is created. Returning null represents a silent note.
	/// </summary>
	SoundInvocation? CreateInvocation(
		double pitchMultiplier,
		double playbackSpeedMultiplier)
	{
		SoundState state = CreateState();
		state.PitchMultiplier = pitchMultiplier;
		state.PlaybackSpeedMultiplier = playbackSpeedMultiplier;

		NoteConfigurationSnapshot configuration =
			SnapshotNoteConfiguration()
			?? throw new InvalidOperationException(
				$"{GetType().Name}.{nameof(SnapshotNoteConfiguration)} returned null.");

		return new SoundInvocation(this, state, configuration);
	}

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
