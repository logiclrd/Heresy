using System;

namespace Heresy.Render.Sounds;

/// <summary>
/// Fully bound executable note. Recursive sounds may replace themselves with a
/// selected child sound while preserving a configuration snapshot assembled by
/// the recursive path.
/// </summary>
public sealed class SoundInvocation
{
	public SoundInvocation(
		ISound sound,
		SoundState state,
		NoteConfigurationSnapshot configuration)
	{
		Sound = sound ?? throw new ArgumentNullException(nameof(sound));
		State = state ?? throw new ArgumentNullException(nameof(state));
		Configuration = configuration
			?? throw new ArgumentNullException(nameof(configuration));
	}

	public ISound Sound { get; }
	public SoundState State { get; }
	public NoteConfigurationSnapshot Configuration { get; }
}
