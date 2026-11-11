using System;
using System.Buffers;
using System.Threading;

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

	// Data-driven frozen tone tables can be compared without resolving or
	// selecting any tones on the PCM thread. Script-driven selectors have no
	// general equivalence proof and stay limited to the same wrapper.
	private sealed record FrozenTone(
		ObjectId SourceId, double PitchMultiplier, InstrumentNnaSetting Nna,
		ObjectId? VolumeEnvelopeId, ObjectId? PitchEnvelopeId,
		ObjectId? PanningEnvelopeId, ObjectId? FilterEnvelopeId,
		FilterInstance? ToneFilter);

	private sealed class FrozenToneMap
	{
		private readonly ObjectId _id;
		private readonly double _divisions;
		private readonly int _offset;
		private readonly InstrumentNnaOverride _nnaOverride;
		private readonly TimeSpan _nnaFadeDuration;
		private readonly int[] _toneTable;
		private readonly FrozenTone[] _tones;

		public FrozenToneMap(InstrumentDefinition definition)
		{
			_id = definition.Id;
			_divisions = definition.Divisions;
			_offset = definition.Offset;
			_nnaOverride = definition.NnaOverride;
			_nnaFadeDuration = definition.NnaFadeDuration;
			_toneTable = definition.ToneTable.ToArray();
			_tones = new FrozenTone[definition.ToneSpecifications.Count];
			for (int i = 0; i < _tones.Length; i++)
			{
				ToneSpecification tone = definition.ToneSpecifications[i];
				_tones[i] = new(tone.SourceId, tone.PitchMultiplier,
					tone.Nna, tone.VolumeEnvelopeId, tone.PitchEnvelopeId,
					tone.PanningEnvelopeId, tone.FilterEnvelopeId,
					tone.FilterConfiguration);
			}
		}

		// Capture the table slot once when a child invocation is bound
		// off the PCM thread. Realtime edit preflight looks up that
		// already-selected slot; it never recomputes a pitch-to-tone choice.
		public int? CaptureSelectedTableIndex(double pitchMultiplier)
		{
			if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier)
				|| !(_divisions > 0.0) || !double.IsFinite(_divisions))
				return null;
			double continuous = Math.Log2(pitchMultiplier) * _divisions
				+ _offset;
			if (!double.IsFinite(continuous)
				|| continuous < int.MinValue || continuous > int.MaxValue)
				return null;
			int index = checked((int)Math.Round(
				continuous, MidpointRounding.AwayFromZero));
			return (uint)index < (uint)_toneTable.Length
				&& (uint)_toneTable[index] < (uint)_tones.Length
				? index : null;
		}

		public bool HasSameSelectionGeometryAs(FrozenToneMap next)
			=> _id == next._id && _divisions == next._divisions
				&& _offset == next._offset
				&& _nnaOverride == next._nnaOverride
				&& _nnaFadeDuration == next._nnaFadeDuration;

		private static bool SameTone(FrozenTone left, FrozenTone right)
			=> left.SourceId == right.SourceId
				&& left.PitchMultiplier == right.PitchMultiplier
				&& left.Nna == right.Nna
				&& left.VolumeEnvelopeId == right.VolumeEnvelopeId
				&& left.PitchEnvelopeId == right.PitchEnvelopeId
				&& left.PanningEnvelopeId == right.PanningEnvelopeId
				&& left.FilterEnvelopeId == right.FilterEnvelopeId
				&& ReferenceEquals(left.ToneFilter, right.ToneFilter);

		public bool HasSameToneAtBoundSlotAs(FrozenToneMap next, int boundSlot)
		{
			if (!HasSameSelectionGeometryAs(next)
				|| (uint)boundSlot >= (uint)_toneTable.Length
				|| (uint)boundSlot >= (uint)next._toneTable.Length)
				return false;
			int oldTone = _toneTable[boundSlot];
			int newTone = next._toneTable[boundSlot];
			return (uint)oldTone < (uint)_tones.Length
				&& (uint)newTone < (uint)next._tones.Length
				&& SameTone(_tones[oldTone], next._tones[newTone]);
		}

		public int ToneCount => _tones.Length;
		public FrozenTone ToneAt(int index) => _tones[index];

		public int? ToneIndexAtBoundSlot(int slot)
		{
			if ((uint)slot >= (uint)_toneTable.Length)
				return null;
			int index = _toneTable[slot];
			return (uint)index < (uint)_tones.Length ? index : null;
		}

		public bool HasSameSelectionAs(FrozenToneMap next)
		{
			if (!HasSameSelectionGeometryAs(next)
				|| _toneTable.Length != next._toneTable.Length
				|| _tones.Length != next._tones.Length)
				return false;
			for (int i = 0; i < _toneTable.Length; i++)
				if (_toneTable[i] != next._toneTable[i])
					return false;
			for (int i = 0; i < _tones.Length; i++)
			{
				FrozenTone left = _tones[i];
				FrozenTone right = next._tones[i];
				if (!SameTone(left, right))
					return false;
			}
			return true;
		}
	}

	private readonly FrozenToneMap? _frozenToneMap;

	// Frozen on the preparation thread, never resolved during PCM. This
	// intentionally handles only a *direct Sample* with an explicitly bound
	// node-based Volume envelope and no tone Filter or other envelopes.
	// More complex Instrument histories need their own migration proof.
	private sealed record PreparedDirectSampleVolume(
		SampleSound Sample, NodeEnvelopeCurve Curve);
	private PreparedDirectSampleVolume?[]? _preparedLiveVolumes;

	internal void PrepareDirectSampleVolumesForLiveAudition()
	{
		if (_frozenToneMap is not { } map
			|| Volatile.Read(ref _preparedLiveVolumes) is not null)
			return;
		PreparedDirectSampleVolume?[] prepared =
			new PreparedDirectSampleVolume?[map.ToneCount];
		for (int i = 0; i < prepared.Length; i++)
		{
			FrozenTone tone = map.ToneAt(i);
			if (tone.VolumeEnvelopeId is not ObjectId volumeId
				|| tone.PitchEnvelopeId.HasValue
				|| tone.PanningEnvelopeId.HasValue
				|| tone.FilterEnvelopeId.HasValue
				|| tone.ToneFilter is not null
				|| !_envelopeResolver.TryResolve(volumeId, out IEnvelopeCurve? curve)
				|| curve is not NodeEnvelopeCurve nodeCurve
				|| !_soundResolver.TryResolve(tone.SourceId, mixdown: false,
					out ISound? source)
				|| source is not SampleSound sample)
				continue;
			prepared[i] = new(sample, nodeCurve);
		}
		Interlocked.CompareExchange(ref _preparedLiveVolumes, prepared, null);
	}

	internal bool TryGetPreparedDirectSampleVolume(
		int? boundSlot, out SampleSound? sample, out NodeEnvelopeCurve? curve)
	{
		sample = null;
		curve = null;
		if (boundSlot is not int slot
			|| _frozenToneMap?.ToneIndexAtBoundSlot(slot) is not int index
			|| Volatile.Read(ref _preparedLiveVolumes) is not { } prepared
			|| prepared[index] is not { } candidate)
			return false;
		sample = candidate.Sample;
		curve = candidate.Curve;
		return true;
	}

	/// <summary>Conservative allocation-free compatibility proof for two
	/// independently prepared wrappers of the same frozen data-driven
	/// Instrument definition. The terminal Sample PCM, selected envelope
	/// objects and release policy are checked separately for each prepared
	/// child invocation before publishing any onset state.</summary>
	/// <summary>The complete selection map need not be equal for a
	/// held note to continue. Different frozen wrappers can be considered
	/// only if they agree on the pitch-to-slot geometry and global NNA;
	/// the particular bound slot is verified separately for every
	/// already-running child before worker preparation.</summary>
	internal bool CanShareFrozenSelectionGeometryWith(InstrumentSound next)
		=> ReferenceEquals(this, next)
			|| _frozenToneMap is { } oldMap
				&& next._frozenToneMap is { } newMap
				&& oldMap.HasSameSelectionGeometryAs(newMap);

	/// <summary>Snapshot the Instrument table index at note creation on
	/// the worker/control boundary. Null identifies a non-data-driven
	/// Instrument or a note without a selected tone.</summary>
	internal int? CaptureFrozenBoundToneIndex(double originalPitch)
		=> _frozenToneMap?.CaptureSelectedTableIndex(originalPitch);

	/// <summary>No pitch-to-tone calculation or Source lookup occurs here:
	/// compare only the immutable selected slot captured at Note On.
	/// An identical scripted wrapper retains its previous eligibility,
	/// but two independent script selectors cannot be equated this way.</summary>
	internal bool CanShareFrozenBoundToneWith(
		InstrumentSound next, int? originalBoundSlot)
		=> ReferenceEquals(this, next)
			|| originalBoundSlot is int index
				&& _frozenToneMap is { } oldMap
				&& next._frozenToneMap is { } newMap
				&& oldMap.HasSameToneAtBoundSlotAs(newMap, index);

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
		// Capture the entire data-driven tone lookup once, outside PCM.
		// Subsequent live phase preflights compare only immutable values.
		_frozenToneMap = new FrozenToneMap(definition);
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
