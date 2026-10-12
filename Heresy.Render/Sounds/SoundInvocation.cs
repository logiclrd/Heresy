using System;

using Heresy.Core.Filters;

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
		NoteConfigurationSnapshot configuration,
		FilterChain? toneFilters = null)
	{
		Sound = sound ?? throw new ArgumentNullException(nameof(sound));
		State = state ?? throw new ArgumentNullException(nameof(state));
		Configuration = configuration
			?? throw new ArgumentNullException(nameof(configuration));
		ToneFilters = toneFilters is null || toneFilters.Count == 0
			? null : new FilterChain(toneFilters.Layers);
	}

	public ISound Sound { get; }
	public SoundState State { get; }
	public NoteConfigurationSnapshot Configuration { get; }

	/// <summary>Per-invocation tone layers, inner to outer; no DSP history.</summary>
	public FilterChain? ToneFilters { get; }
}
