using System;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Render.Envelopes;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

namespace Heresy.Render.Instruments;

/// <summary>
/// Recursive executable instrument. Note binding selects a ToneSpecification
/// from the instrument tone table, composes its pitch multiplier, recursively
/// binds the selected source sound, and overlays any tone-level envelope
/// references onto the child's captured note configuration.
/// </summary>
public sealed class InstrumentSound : ISound
{
	private sealed class DirectState : SoundState
	{
		public DirectState(SoundInvocation? invocation)
			=> Invocation = invocation;

		public SoundInvocation? Invocation { get; }
	}

	private readonly InstrumentDefinition _definition;
	private readonly ISoundResolver _soundResolver;
	private readonly IEnvelopeCurveResolver _envelopeResolver;

	public InstrumentSound(
		InstrumentDefinition definition,
		ISoundResolver soundResolver,
		IEnvelopeCurveResolver envelopeResolver)
	{
		_definition = definition
			?? throw new ArgumentNullException(nameof(definition));
		_soundResolver = soundResolver
			?? throw new ArgumentNullException(nameof(soundResolver));
		_envelopeResolver = envelopeResolver
			?? throw new ArgumentNullException(nameof(envelopeResolver));
	}

	/// <summary>
	/// Neutral-pitch compatibility snapshot for direct ISound callers. Normal
	/// note playback uses CreateInvocation so pitch-dependent tone selection is
	/// performed before the PlaybackVoice exists.
	/// </summary>
	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> CreateInvocation(1.0, 1.0)?.Configuration
			?? NoteConfigurationSnapshot.Default;

	/// <summary>
	/// Neutral-pitch compatibility state for direct ISound callers. PlaybackSession
	/// uses CreateInvocation and therefore receives the recursively flattened
	/// terminal sound/state directly.
	/// </summary>
	public SoundState CreateState()
		=> new DirectState(CreateInvocation(1.0, 1.0));

	public SoundInvocation? CreateInvocation(
		double pitchMultiplier,
		double playbackSpeedMultiplier)
	{
		ValidatePositiveFinite(
			pitchMultiplier,
			nameof(pitchMultiplier));
		ValidatePositiveFinite(
			playbackSpeedMultiplier,
			nameof(playbackSpeedMultiplier));

		ToneSpecification? tone = ResolveTone(pitchMultiplier);
		if (tone is null)
			return null;

		ValidatePositiveFinite(
			tone.PitchMultiplier,
			nameof(ToneSpecification.PitchMultiplier));

		if (!_soundResolver.TryResolve(
			tone.SourceId,
			mixdown: false,
			out ISound? child)
			|| child is null)
		{
			return null;
		}

		double childPitch = pitchMultiplier * tone.PitchMultiplier;
		ValidatePositiveFinite(childPitch, nameof(pitchMultiplier));

		SoundInvocation? childInvocation = child.CreateInvocation(
			childPitch,
			playbackSpeedMultiplier);
		if (childInvocation is null)
			return null;

		EnvelopeConfigurationSnapshot envelopes =
			ResolveEnvelopes(
				tone,
				childInvocation.Configuration.Envelopes);

		NoteConfigurationSnapshot configuration =
			new(
				childInvocation.Configuration.NewNotePolicy,
				childInvocation.Configuration.NoteFadeDuration,
				childInvocation.Configuration.NewNoteFadeDuration,
				envelopes);

		return new SoundInvocation(
			childInvocation.Sound,
			childInvocation.State,
			configuration);
	}

	public long? GetEndFrameExclusive(
		RenderContext context,
		SoundState state)
	{
		DirectState direct = ValidateDirectState(state);
		SoundInvocation? invocation = direct.Invocation;
		if (invocation is null)
			return 0;

		return invocation.Sound.GetEndFrameExclusive(
			context,
			invocation.State);
	}

	public void Render(
		RenderContext context,
		SoundState state,
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		DirectState direct = ValidateDirectState(state);
		SoundInvocation? invocation = direct.Invocation;
		if (invocation is null)
			return;

		invocation.Sound.Render(
			context,
			invocation.State,
			startFrame,
			frameCount,
			destination);
	}

	private ToneSpecification? ResolveTone(double pitchMultiplier)
	{
		double divisions = _definition.Divisions;
		if (!(divisions > 0.0)
			|| double.IsNaN(divisions)
			|| double.IsInfinity(divisions))
		{
			throw new InvalidOperationException(
				"Instrument divisions must be positive and finite.");
		}

		double continuousIndex =
			Math.Log2(pitchMultiplier) * divisions
			+ definition.Offset;
		if (continuousIndex < int.MinValue
			|| continuousIndex > int.MaxValue)
		{
			return null;
		}

		int toneIndex = checked((int)Math.Round(
			continuousIndex,
			MidpointRounding.AwayFromZero));
		if ((uint)toneIndex >= (uint)definition.ToneTable.Count)
			return null;

		int specificationIndex = definition.ToneTable[toneIndex];
		if (specificationIndex == -1)
			return null;
		if ((uint)specificationIndex
			>= (uint)_definition.ToneSpecifications.Count)
		{
			throw new InvalidOperationException(
				$"Tone table entry {toneIndex} refers to invalid tone specification {specificationIndex}.");
		}

		return definition.ToneSpecifications[specificationIndex];
	}

	private EnvelopeConfigurationSnapshot ResolveEnvelopes(
		ToneSpecification tone,
		EnvelopeConfigurationSnapshot inherited)
		=> new(
			Volume: ResolveEnvelope(
				tone.VolumeEnvelopeId,
				inherited.Volume),
			Pitch: ResolveEnvelope(
				tone.PitchEnvelopeId,
				inherited.Pitch),
			Panning: ResolveEnvelope(
				tone.PanningEnvelopeId,
				inherited.Panning),
			Filter: ResolveEnvelope(
				tone.FilterEnvelopeId,
				inherited.Filter));

	private IEnvelopeCurve? ResolveEnvelope(
		ObjectId? envelopeId,
		IEnvelopeCurve? inherited)
	{
		if (!envelopeId.HasValue)
			return inherited;

		return _envelopeResolver.TryResolve(
			envelopeId.Value,
			out IEnvelopeCurve? curve)
			? curve
			: null;
	}

	private static DirectState ValidateDirectState(SoundState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (state is not DirectState direct)
		{
			throw new ArgumentException(
				$"State must be created by {nameof(InstrumentSound)}.",
				nameof(state));
		}
		return direct;
	}

	private static void ValidatePositiveFinite(
		double value,
		string parameterName)
	{
		if (!(value > 0.0)
			|| double.IsNaN(value)
			|| double.IsInfinity(value))
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}
	}
}
