using System;
using System.Buffers;

using Heresy.Core.Filters;
using Heresy.Render.Filters;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Render.Envelopes;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
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
		private StreamingFilterChain? _filters;
		public DirectState(SoundInvocation? invocation)
			=> Invocation = invocation;
		public SoundInvocation? Invocation { get; }
		public StreamingFilterChain? GetFilters(RenderContext context)
		{
			FilterChain? configuration = Invocation?.ToneFilters;
			if (configuration is null)
				return null;
			int rate = context.Configuration.SampleRate;
			int outputs = context.Configuration.OutputChannelCount;
			if (_filters is not null)
			{
				if (_filters.SampleRate != rate || _filters.OutputChannelCount != outputs)
					throw new InvalidOperationException(
						"One Instrument invocation cannot change rendering format.");
				return _filters;
			}
			return _filters = new StreamingFilterChain(configuration, rate, outputs);
		}
	}

	private readonly Func<double, ToneSpecification?> _selectTone;
    private readonly Func<double, ToneSpecification, InstrumentNnaSetting> _selectNna;
	private readonly ISoundResolver _soundResolver;
	private readonly IEnvelopeCurveResolver _envelopeResolver;

	public InstrumentSound(
		InstrumentDefinition definition,
		ISoundResolver soundResolver,
		IEnvelopeCurveResolver envelopeResolver)
        : this(pitch => SelectTone(definition, pitch),
            soundResolver, envelopeResolver,
            (pitch, tone) => definition.ResolveNna(pitch, tone))
	{
		ArgumentNullException.ThrowIfNull(definition);
	}

	/// <summary>
	/// A selector prepared before rendering. No Roslyn compilation or
	/// dynamic source discovery occurs inside the renderer.
	/// </summary>
	public InstrumentSound(
		Func<double, ToneSpecification?> selectTone,
        ISoundResolver soundResolver,
        IEnvelopeCurveResolver envelopeResolver,
        Func<double, ToneSpecification, InstrumentNnaSetting>? selectNna = null)
    {
		_selectTone = selectTone
            ?? throw new ArgumentNullException(nameof(selectTone));
        _selectNna = selectNna ?? ((_, tone) => tone.Nna);
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

		// Instrument binding flattens to the terminal sound/state, so
		// SampleSound would otherwise still cut the PCM at Note Off even
		// while the overlaid volume envelope is releasing. Select this
		// per-invocation policy only after all envelope overlays at this
		// level are resolved. An outer nested Instrument may override it.
		if (childInvocation.Sound is SampleSound
			&& childInvocation.State is SampleSoundState sampleState)
		{
			sampleState.ReleaseIsControlledByVolumeEnvelope =
				envelopes.Volume is not null;
		}

        // Resolve the hierarchy at this invocation's original incoming
        // pitch (not its child-transposed pitch). The selected child is
        // already snapshotted, so an outer explicit NNA wins over it.
        InstrumentNnaSetting selectedNna = _selectNna(pitchMultiplier, tone);
        selectedNna.Validate();
        NewNotePolicy resolvedPolicy = selectedNna.Action switch
        {
            InstrumentNnaAction.Inherit => childInvocation.Configuration.NewNotePolicy,
            InstrumentNnaAction.Cut => NewNotePolicy.Cut,
            InstrumentNnaAction.Continue => NewNotePolicy.Continue,
            InstrumentNnaAction.Off => NewNotePolicy.Off,
            InstrumentNnaAction.Fade => NewNotePolicy.Fade(
                selectedNna.FadeDuration!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(selectedNna)),
        };
        TimeSpan? selectedFadeDuration = selectedNna.Action == InstrumentNnaAction.Fade
            ? selectedNna.FadeDuration
            : childInvocation.Configuration.NewNoteFadeDuration;
        NoteConfigurationSnapshot configuration =
            new(resolvedPolicy,
                childInvocation.Configuration.NoteFadeDuration,
                selectedFadeDuration,
                envelopes);

		// Compose tone DSP from the selected terminal source outward. Do not
		// change the terminal ISound or its SoundState: native source-frame
		// offsets, Note Off and flattened source identities must survive.
		FilterChain? filters = childInvocation.ToneFilters;
		if (tone.FilterConfiguration is FilterInstance outer)
			filters = filters is null
				? new FilterChain(new[] { outer })
				: filters.Append(outer);

		return new SoundInvocation(
			childInvocation.Sound,
			childInvocation.State,
			configuration,
			filters);
	}

	public long? GetEndFrameExclusive(
		RenderContext context,
		SoundState state)
	{
		DirectState direct = ValidateDirectState(state);
		SoundInvocation? invocation = direct.Invocation;
		if (invocation is null)
			return 0;

		long? sourceEnd = invocation.Sound.GetEndFrameExclusive(
			context, invocation.State);
		if (!sourceEnd.HasValue)
			return null;
		int tail = direct.GetFilters(context)?.PreparedTailFrames ?? 0;
		return sourceEnd.Value > long.MaxValue - tail
			? long.MaxValue : sourceEnd.Value + tail;
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

		StreamingFilterChain? filters = direct.GetFilters(context);
		if (filters is null)
		{
			invocation.Sound.Render(context, invocation.State,
				startFrame, frameCount, destination);
			return;
		}

		int channels = context.Configuration.OutputChannelCount;
		int count = checked(frameCount * channels);
		float[] rented = ArrayPool<float>.Shared.Rent(count);
		try
		{
			Span<float> isolated = rented.AsSpan(0, count);
			isolated.Clear();
			long? end = invocation.Sound.GetEndFrameExclusive(
				context, invocation.State);
			int sourceFrames = end.HasValue
				? (int)Math.Min(frameCount, Math.Max(0, end.Value - startFrame))
				: frameCount;
			if (sourceFrames > 0)
				invocation.Sound.Render(context, invocation.State, startFrame,
					sourceFrames, isolated.Slice(0, sourceFrames * channels));
			filters.ProcessInterleaved(isolated);
			for (int i = 0; i < count; i++)
				destination[i] += isolated[i];
		}
		finally { ArrayPool<float>.Shared.Return(rented); }
	}

	private ToneSpecification? ResolveTone(double pitchMultiplier)
		=> _selectTone(pitchMultiplier);

	/// <summary>Pure tone-table selection shared by normal instrument
	/// binding and recursive playback's indirect-source preflight.
	/// Only the selected tone is examined; unselected branches are inert.</summary>
	public static ToneSpecification? SelectTone(
		InstrumentDefinition definition, double pitchMultiplier)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ValidatePositiveFinite(pitchMultiplier, nameof(pitchMultiplier));
		double divisions = definition.Divisions;
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
			return null;

		int toneIndex = checked((int)Math.Round(
			continuousIndex, MidpointRounding.AwayFromZero));
		if ((uint)toneIndex >= (uint)definition.ToneTable.Count)
			return null;
		int specificationIndex = definition.ToneTable[toneIndex];
		if (specificationIndex == -1)
			return null;
		if ((uint)specificationIndex >= (uint)definition.ToneSpecifications.Count)
			throw new InvalidOperationException(
				$"Tone table entry {toneIndex} refers to invalid tone specification {specificationIndex}.");
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
