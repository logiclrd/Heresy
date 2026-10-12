using System;
using System.Buffers;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Core.Filters;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// Sequential event-to-PCM renderer. Immutable schedules and incremental
/// live notes share the same voice and effect state. Physical channels own one current
/// voice each; displaced Continue/Off/Fade voices migrate into VirtualVoices.
/// </summary>
public sealed class PlaybackSession
{
	/// <summary>Stateful final-speaker filters. One bank belongs to the
	/// master output session, never to a private Pattern/Sequence mixer.
	/// The one-pole RC coefficient is 1-exp(-2π·cutoff/sampleRate), with
	/// independent low-pass histories for each speaker. A high-pass feed
	/// is the input minus its low-pass component.</summary>
	private sealed class OutputSpeakerFilterBank
	{
		private const double SilenceThreshold = 1e-7;
		private readonly OutputFilterType[] _mode;
		private readonly double[] _alpha;
		private readonly double[] _low;

		public OutputSpeakerFilterBank(RenderConfiguration configuration)
		{
			int count = configuration.OutputChannelCount;
			_mode = new OutputFilterType[count];
			_alpha = new double[count];
			_low = new double[count];
			for (int speaker = 0; speaker < count; speaker++)
			{
				OutputChannelConfiguration channel =
					configuration.OutputChannels[speaker];
				_mode[speaker] = channel.FilterType;
				if (channel.FilterType is not OutputFilterType.None)
					_alpha[speaker] = 1.0 - Math.Exp(
						-2.0 * Math.PI * channel.CutoffHz!.Value
							/ configuration.SampleRate);
			}
		}

		public bool HasTail
		{
			get
			{
				for (int i = 0; i < _low.Length; i++)
					if (Math.Abs(_low[i]) > SilenceThreshold)
						return true;
				return false;
			}
		}

		public void Apply(Span<float> samples, int channelCount)
		{
			for (int sampleIndex = 0; sampleIndex < samples.Length;
				sampleIndex += channelCount)
				for (int speaker = 0; speaker < channelCount; speaker++)
				{
					OutputFilterType type = _mode[speaker];
					if (type == OutputFilterType.None)
						continue;
					double input = samples[sampleIndex + speaker];
					double low = _low[speaker]
						+ _alpha[speaker] * (input - _low[speaker]);
					// Clear only *decaying silence*, never a very quiet
					// accumulator under nonzero input. Otherwise an
					// extremely low cutoff can get stuck at zero forever.
					// The bounded export tail avoids infinite denormals.
					if (input == 0.0 && Math.Abs(low) <= SilenceThreshold)
						low = 0.0;
					_low[speaker] = low;
					samples[sampleIndex + speaker] = (float)(
						type == OutputFilterType.LowPass
							? low : input - low);
				}
		}
	}

	private sealed class ActiveTempoRamp
	{
		public required long StartFrame { get; init; }
		public required double TrackerTicks { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
	}
	private sealed class ActiveGlobalVolumeSlide
	{
		public required long StartFrame { get; init; }
		public required int TicksPerRow { get; init; }
		public required LinearRowPlaybackOperator Operator { get; init; }
	}

	/// <summary>Optional bounded control-side diagnostic sink. Production
	/// recursive playback wires this to SequencingDiagnosticLog.</summary>
	public Action<string>? NoteFilterCommandRejected { get; set; }

	/// <summary>Already-prepared Node/Script Envelope curve provider,
	/// installed by the production snapshot factory before PCM playback.</summary>
	public IEnvelopeCurveResolver? NoteFilterEnvelopeResolver { get; set; }

	/// <summary>Control-side diagnostic output for invalid host-channel
	/// Filter commands. The prepared playback factory rate-limits it.</summary>
	public Action<string>? ChannelFilterCommandRejected { get; set; }

	private readonly RenderContext _context;
	private readonly OutputSpeakerFilterBank? _outputSpeakerFilters;

	// A physical host is identified by its actual output host index, never
	// its nested Pattern/Sequence playback owner. Scoped virtual channels
	// use the (owner, virtual ID) pair to avoid cross-cursor aliasing.
	private readonly SortedDictionary<int, ChannelOutputFilterState>
		_physicalOutputFilters = [];
	private readonly SortedDictionary<uint, ChannelOutputFilterState>
		_targetedVirtualOutputFilters = [];
	private readonly SortedDictionary<(long Owner, uint Id), ChannelOutputFilterState>
		_scopedVirtualOutputFilters = [];
	private readonly NoteSchedule _schedule;
	private readonly ISoundResolver _soundResolver;
	private readonly TrackerTickClock _tickClock;
	private readonly SortedDictionary<(long Owner, int Host), PlaybackChannelState>
		_channels = [];
	// A completed producer's logical playback channels are reclaimed only
	// after all its voices (including displaced NNA) and tails finish.
	private readonly HashSet<long> _retiredPhysicalScopes = [];
	// Scope-unique controllers outlive the instigating note's assignment
	// to a logical channel. Descendant voices hold direct references.
	private readonly Dictionary<long, FlattenedSourceVolume> _sourceVolumes = [];
	private readonly SortedDictionary<uint, PlaybackChannelState> _targetedVirtualChannels = [];
	// A Pattern's virtual ID is local to its invocation, not a global
	// playback channel. The legacy live-preview dictionary above remains
	// unchanged for tracker-key audition.
	private readonly SortedDictionary<(long Owner, uint Id), PlaybackChannelState>
		_scopedVirtualChannels = [];
	private readonly List<PlaybackVoice> _virtualVoices = [];

	// A finished or cut NNA voice may leave residual DSP state on its own
	// forked dynamic output host. Keep processing that zero-input tail after
	// the source voice is removed; never redirect it to its original host.
	private readonly List<ChannelOutputFilterState> _displacedOutputTails = [];
	// Set only while applying one physical event from the coroutine.
	// Live previews and prebuilt schedules have no cancelable cursor owner.
	private long? _activePatternCursorId;
	private readonly SortedDictionary<(long Owner, int Host), ActiveGlobalVolumeSlide>
		_globalVolumeSlides = [];
	private readonly PlaybackOperatorCollection _globalOperators = new();
	private readonly List<ActiveParametricEffect> _nativeGlobal = [];

	private ActiveTempoRamp? _activeTempoRamp;
	private int _nextEventIndex;
	private long _nextFrame;
	private ulong _nextVoiceModulationSeed = 0x4845524553590001UL;
	private double _tempo = SequencingConstants.DefaultTempo;
	private int _speed = SequencingConstants.DefaultSpeed;
	private double _globalVolume = 1.0;
	private bool _inputEnded;

	public PlaybackSession(
		RenderContext context,
		NoteSchedule schedule,
		ISoundResolver soundResolver,
		double initialTempo = SequencingConstants.DefaultTempo,
		bool applyFinalSpeakerFilters = true)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
		_soundResolver = soundResolver ?? throw new ArgumentNullException(nameof(soundResolver));
		// A private recursive mixer contributes an *unfiltered* speaker
		// buffer to its parent. Only the final output applies the shared
		// configuration's speaker filters, exactly once after summing all
		// voices, NNA tails and the global volume.
		if (applyFinalSpeakerFilters)
			foreach (OutputChannelConfiguration channel
				in context.Configuration.OutputChannels)
				if (channel.FilterType != OutputFilterType.None)
				{
					_outputSpeakerFilters = new OutputSpeakerFilterBank(
						context.Configuration);
					break;
				}
		if (!(initialTempo > 0.0) || !double.IsFinite(initialTempo))
			throw new ArgumentOutOfRangeException(nameof(initialTempo));
		_tempo = initialTempo;
		_tickClock = new TrackerTickClock(
			_schedule, _context.Configuration.SampleRate, initialTempo);
	}

	/// <summary>Optional non-UI reporting hook. Invoked on the PCM rendering
	/// worker only for an actual seek/retrigger on a ReplayRequired sound.</summary>
	public Action<string, long>? ReplayRequiredSeekObserved { get; set; }

	/// <summary>Optional inherited pitch factor for each native private-session
	/// frame. It bends individual child notes, not this session's tracker clock.
	/// The private renderer owns the mapping across Oxx/Qxy reconstruction.</summary>
	public Func<long, double>? InheritedPitchAtFrame { get; set; }

	public long NextFrame => _nextFrame;

	public int SampleRate => _context.Configuration.SampleRate;

	public int OutputChannelCount => _context.Configuration.OutputChannelCount;

	public double GlobalVolume => _globalVolume;

	public int ActiveGlobalOperatorCount => _globalOperators.Count;


	public bool InputEnded => _inputEnded;

	/// <summary>Configure a persistent output Filter on a real physical
	/// host (across all nested playback owners). Null removes the slot.
	/// Native effects use the same host slot via a separate command path.
	/// Preparation completes before the old state is replaced.</summary>
	public void ConfigurePhysicalChannelOutputFilter(int host, FilterInstance? use)
	{
		if (host < 0) throw new ArgumentOutOfRangeException(nameof(host));
		ChannelOutputFilterState? prepared = use is null ? null
			: new ChannelOutputFilterState(use, SampleRate, OutputChannelCount);
		if (prepared is null)
			_physicalOutputFilters.Remove(host);
		else
			_physicalOutputFilters[host] = prepared;
	}

	/// <summary>Virtual output host identified by ID and, when set, its
	/// invocation owner. Null owner is the unscoped targeted virtual host.</summary>
	public void ConfigureVirtualChannelOutputFilter(
		uint virtualId, FilterInstance? use, long? scopedOwner = null)
	{
		if (scopedOwner is < 0)
			throw new ArgumentOutOfRangeException(nameof(scopedOwner));
		ChannelOutputFilterState? prepared = use is null ? null
			: new ChannelOutputFilterState(use, SampleRate, OutputChannelCount);
		if (scopedOwner is long owner)
		{
			if (prepared is null)
				_scopedVirtualOutputFilters.Remove((owner, virtualId));
			else
				_scopedVirtualOutputFilters[(owner, virtualId)] = prepared;
		}
		else if (prepared is null)
			_targetedVirtualOutputFilters.Remove(virtualId);
		else
			_targetedVirtualOutputFilters[virtualId] = prepared;
	}

	public bool IsQuiescent
	{
		get
		{
			if (!_inputEnded
				|| _nextEventIndex < _schedule.Count
				|| _virtualVoices.Count != 0
				|| _displacedOutputTails.Count != 0
				|| _outputSpeakerFilters?.HasTail == true
				|| AnyChannelOutputFilterHasTail())
			{
				return false;
			}

			foreach (PlaybackChannelState channel in _channels.Values)
			{
				if (channel.CurrentVoice is not null
					|| channel.AntiClickTail.IsActive)
				{
					return false;
				}
			}

			foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			{
				if (channel.CurrentVoice is not null
					|| channel.AntiClickTail.IsActive)
				{
					return false;
				}
			}
			foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			{
				if (channel.CurrentVoice is not null
					|| channel.AntiClickTail.IsActive)
					return false;
			}
			return true;
		}
	}

	public bool HasIndefiniteActiveVoices
	{
		get
		{
			if (!_inputEnded)
				return false;

			foreach (PlaybackChannelState channel in _channels.Values)
			{
				if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
						voice,
						_nextFrame).HasValue)
				{
					return true;
				}
			}

			foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			{
				if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
						voice,
						_nextFrame).HasValue)
				{
					return true;
				}
			}

			foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			{
				if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
						voice, _nextFrame).HasValue)
					return true;
			}
			foreach (PlaybackVoice voice in _virtualVoices)
			{
				if (voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
					voice,
					_nextFrame).HasValue)
				{
					return true;
				}
			}

			return false;
		}
	}


	public double BaselineTempo => _tempo;

	public double GetEffectiveTempo(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		UpdateTempoRamp(absoluteFrame);
		return _tempo
			+ _globalOperators.GetTotalDelta(
				PlaybackParameter.Tempo);
	}

	/// <summary>
	/// Checks all rendered voices by their actual bound sound identity.
	/// Detached anti-click tails retain PCM residue, not the original sound.
	/// Used on the single rendering worker to retire transient sources.
	/// </summary>
	public bool HasActiveSound(ISound sound)
	{
		ArgumentNullException.ThrowIfNull(sound);
		foreach (PlaybackChannelState channel in _channels.Values)
			if (ReferenceEquals(channel.CurrentVoice?.Sound, sound))
				return true;
		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			if (ReferenceEquals(channel.CurrentVoice?.Sound, sound))
				return true;
		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			if (ReferenceEquals(channel.CurrentVoice?.Sound, sound))
				return true;
		foreach (PlaybackVoice voice in _virtualVoices)
			if (ReferenceEquals(voice.Sound, sound))
				return true;
		return false;
	}

	public IReadOnlyList<PlaybackVoice> VirtualVoices => _virtualVoices;

	/// <summary>Live source-controller registrations still owned by
	/// active producer scopes. Descendants retain their controller objects
	/// directly after a producer is retired.</summary>
	public int RetainedFlattenedSourceControllerCount => _sourceVolumes.Count;

	/// <summary>Controls only voices started by the indicated flattened
	/// source (including its descendants and displaced NNA voices).
	/// Natural producer completion deliberately does not call this.</summary>
	public void ApplyFlattenedScopeAction(
		long scopeId, NoteDisplacementAction action)
	{
		if (scopeId <= 0)
			throw new ArgumentOutOfRangeException(nameof(scopeId));
		if (action == NoteDisplacementAction.Continue)
			return;
		long frame = _nextFrame;
		foreach (PlaybackChannelState channel in _channels.Values)
			ApplySourceActionToChannel(channel, scopeId, action, frame);
		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			ApplySourceActionToChannel(channel, scopeId, action, frame);
		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			ApplySourceActionToChannel(channel, scopeId, action, frame);
		for (int i = _virtualVoices.Count - 1; i >= 0; i--)
		{
			PlaybackVoice voice = _virtualVoices[i];
			if (!VoiceBelongsToFlattenedSource(voice, scopeId))
				continue;
			if (action == NoteDisplacementAction.Cut)
			{
				voice.AddCutTo(GetChannelState(
					voice.OriginPhysicalChannel,
					voice.OriginPhysicalPlaybackOwner).AntiClickTail);
				RetireDisplacedVoiceAt(i);
			}
			else if (action == NoteDisplacementAction.Off)
				voice.ApplyNoteOff(frame, SampleRate);
			else if (action == NoteDisplacementAction.Fade)
				voice.RequestNoteFade(frame, SampleRate,
					voice.Configuration.NewNoteFadeDuration);
		}
	}

	private static bool VoiceBelongsToFlattenedSource(
		PlaybackVoice voice, long scopeId)
	{
		foreach (FlattenedSourceVolume source in voice.EnclosingSourceVolumes)
			if (source.ScopeId == scopeId)
				return true;
		return false;
	}

	private void ApplySourceActionToChannel(
		PlaybackChannelState channel, long scopeId,
		NoteDisplacementAction action, long frame)
	{
		if (channel.CurrentFlattenedSource?.ScopeId == scopeId
			&& action == NoteDisplacementAction.Cut)
			channel.CurrentFlattenedSource = null;
		PlaybackVoice? voice = channel.CurrentVoice;
		if (voice is null || !VoiceBelongsToFlattenedSource(voice, scopeId))
			return;
		switch (action)
		{
			case NoteDisplacementAction.Cut:
				channel.CutCurrentVoice();
				break;
			case NoteDisplacementAction.Off:
				voice.ApplyNoteOff(frame, SampleRate);
				CullFinishedCurrentVoice(channel, frame);
				break;
			case NoteDisplacementAction.Fade:
				// Flattened-source S76 is an explicit new-note action.
				// Its descendants use their new-note fade duration, not
				// their independent ordinary note-fade duration.
				voice.RequestNoteFade(frame, SampleRate,
					voice.Configuration.NewNoteFadeDuration);
				break;
		}
	}

	/// <summary>Diagnostic count of non-root logical channel entries,
	/// including scopes retained for sounding voices or anti-click tails.</summary>
	public int RetainedScopedPhysicalChannelCount
	{
		get
		{
			int count = 0;
			foreach (var key in _channels.Keys)
				if (key.Owner != 0)
					count++;
			return count;
		}
	}

	/// <summary>The producer is gone. Its existing voices and cut tails
	/// remain audible; its channel state may now be reclaimed once silent.</summary>
	public void RetirePhysicalScope(long scopeId)
	{
		if (scopeId <= 0)
			throw new ArgumentOutOfRangeException(nameof(scopeId));
		_retiredPhysicalScopes.Add(scopeId);
		// No new child voice may be created once its entire producer tree
		// has retired; existing voices hold the controller directly.
		_sourceVolumes.Remove(scopeId);
		ReclaimRetiredPhysicalScopes();
	}

	private void ReclaimRetiredPhysicalScopes()
	{
		if (_retiredPhysicalScopes.Count == 0)
			return;
		foreach (long scopeId in new List<long>(_retiredPhysicalScopes))
		{
			bool retained = false;
			foreach (PlaybackVoice voice in _virtualVoices)
				if (voice.OriginPhysicalPlaybackOwner == scopeId)
				{
					retained = true;
					break;
				}
			if (retained)
				continue;
			List<(long Owner, int Host)> candidates = [];
			foreach (var pair in _channels)
			{
				if (pair.Key.Owner != scopeId)
					continue;
				if (pair.Value.CurrentVoice is not null
					|| pair.Value.AntiClickTail.IsActive)
				{
					retained = true;
					break;
				}
				candidates.Add(pair.Key);
			}
			if (retained)
				continue;
			foreach (var key in candidates)
				_channels.Remove(key);
			_retiredPhysicalScopes.Remove(scopeId);
		}
	}

	public bool TryGetChannelState(int channel, out PlaybackChannelState? state)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		return _channels.TryGetValue((0, channel), out state);
	}

	public PlaybackChannelState GetChannelState(int channel)
		=> GetChannelState(channel, 0);

	/// <summary>Recalled note volume as of the requested future musical
	/// boundary, including the currently playing voice's volume curve.</summary>
	public double GetRememberedNoteVolume(int host, long owner, long frame)
	{
		PlaybackChannelState channel = GetChannelState(host, owner);
		return channel.CurrentFlattenedSource?.Read(frame)
			?? channel.CurrentVoice?.GetBaseNoteVolume(frame)
			?? channel.NoteVolume;
	}

	/// <summary>Owner 0 selects the ordinary parent physical channel;
	/// positive owners have independent logical channels at that host.</summary>
	public PlaybackChannelState GetChannelState(int channel, long owner)
	{
		if (channel < 0)
			throw new ArgumentOutOfRangeException(nameof(channel));

		if (owner < 0)
			throw new ArgumentOutOfRangeException(nameof(owner));
		if (!_channels.TryGetValue((owner, channel), out PlaybackChannelState? state))
		{
			ulong panbrelloSeed = unchecked(
				0x50414E4252454C4CUL
				+ 0x9E3779B97F4A7C15UL
					* ((ulong)(uint)channel + 1UL)
				+ (ulong)owner * 0xD1B54A32D192ED03UL);

			state = new PlaybackChannelState(
				_context.Configuration.OutputChannelCount,
				_context.Configuration.SampleRate,
				_tickClock,
				panbrelloSeed);
			_channels.Add((owner, channel), state);
		}

		return state;
	}

	private PlaybackChannelState GetVirtualChannelState(uint channelId)
	{
		if (!_targetedVirtualChannels.TryGetValue(
			channelId,
			out PlaybackChannelState? state))
		{
			ulong panbrelloSeed = unchecked(
				0x5649525455414C00UL
					+ 0x9E3779B97F4A7C15UL
						* ((ulong)channelId + 1UL));

			state = new PlaybackChannelState(
				_context.Configuration.OutputChannelCount,
				_context.Configuration.SampleRate,
				_tickClock,
				panbrelloSeed);
			_targetedVirtualChannels.Add(
				channelId,
				state);
		}

		return state;
	}

	/// <summary>
	/// Request normal note-fade semantics for voices presently active in
	/// this session. A recursive private-clock owner invokes this at its
	/// prepared frame when the parent applies an NNA/past-note Fade.
	/// No sequencing work is performed here.
	/// </summary>
	public void RequestFadeOfActiveVoices(bool newNoteDisplacement = false)
	{
		void Fade(PlaybackVoice? voice)
		{
			if (voice is null)
				return;
			if (newNoteDisplacement)
				voice.RequestNoteFade(_nextFrame, SampleRate,
					voice.Configuration.NewNoteFadeDuration);
			else
				voice.RequestNoteFade(_nextFrame, SampleRate);
		}
		foreach (PlaybackChannelState channel in _channels.Values)
			Fade(channel.CurrentVoice);
		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			Fade(channel.CurrentVoice);
		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			Fade(channel.CurrentVoice);
		foreach (PlaybackVoice voice in _virtualVoices)
			Fade(voice);
	}

	public void EndInput()
	{
		if (_inputEnded)
			return;

		ProcessEventsThrough(_nextFrame);
		if (_nextEventIndex < _schedule.Count)
		{
			throw new InvalidOperationException(
				"Playback input cannot end before every scheduled event has been reached.");
		}

		foreach (PlaybackChannelState channel in _channels.Values)
		{
			channel.CurrentVoice?.ApplyNoteOff(
				_nextFrame,
				_context.Configuration.SampleRate);
			CullFinishedCurrentVoice(
				channel,
				_nextFrame);
		}

		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
		{
			channel.CurrentVoice?.ApplyNoteOff(
				_nextFrame,
				_context.Configuration.SampleRate);
			CullFinishedCurrentVoice(
				channel,
				_nextFrame);
		}

		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
		{
			channel.CurrentVoice?.ApplyNoteOff(
				_nextFrame, _context.Configuration.SampleRate);
			CullFinishedCurrentVoice(channel, _nextFrame);
		}
		foreach (PlaybackVoice voice in _virtualVoices)
		{
			voice.ApplyNoteOff(
				_nextFrame,
				_context.Configuration.SampleRate);
		}

		_inputEnded = true;
	}

	public void CutIndefiniteActiveVoicesAfterEndInput()
	{
		if (!_inputEnded)
		{
			throw new InvalidOperationException(
				"Indefinite voices can only be cut after playback input has ended.");
		}

		foreach (PlaybackChannelState channel in _channels.Values)
		{
			if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
					voice,
					_nextFrame).HasValue)
			{
				channel.CutCurrentVoice();
			}
		}

		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
		{
			if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
					voice,
					_nextFrame).HasValue)
			{
				channel.CutCurrentVoice();
			}
		}

		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
		{
			if (channel.CurrentVoice is PlaybackVoice voice
				&& voice.Sound is not IStreamingFiniteSound
				&& !GetEffectiveVoiceEndFrameExclusive(
					voice, _nextFrame).HasValue)
				channel.CutCurrentVoice();
		}

		for (int index = _virtualVoices.Count - 1;
			index >= 0;
			index--)
		{
			PlaybackVoice voice = _virtualVoices[index];
			if (voice.Sound is IStreamingFiniteSound
				|| GetEffectiveVoiceEndFrameExclusive(
					voice, _nextFrame).HasValue)
			{
				continue;
			}

			PlaybackChannelState origin =
				GetChannelState(
					voice.OriginPhysicalChannel,
					voice.OriginPhysicalPlaybackOwner);
			voice.AddCutTo(
				origin.AntiClickTail);
			RetireDisplacedVoiceAt(index);
		}
	}

	/// <summary>
	/// Apply an incremental Pattern event at the current output frame.
	/// Virtual(id) is local to invocationId, and broadcasts affect only
	/// voices started at strictly earlier frames. Ordinary previews keep
	/// their independent legacy virtual-channel namespace.
	/// </summary>
	public void ApplyScopedEvent(
		long invocationId, ChannelTarget target,
		IReadOnlyList<NoteCommand> commands,
		long physicalPlaybackOwner = 0)
	{
		if (invocationId < 0)
			throw new ArgumentOutOfRangeException(nameof(invocationId));
		ArgumentNullException.ThrowIfNull(commands);
		switch (target.Kind)
		{
			case ChannelTargetKind.Virtual:
				ApplyVirtualCommands(GetScopedChannel(
					invocationId, target.VirtualChannelId),
					commands, _nextFrame, cutIndefiniteAfterNoteOff: false,
					virtualChannelId: target.VirtualChannelId,
					scopedOwner: invocationId);
				return;
			case ChannelTargetKind.AllVirtualInScope:
			case ChannelTargetKind.AllVirtual:
				ApplyVirtualBroadcast(invocationId,
					target.Kind == ChannelTargetKind.AllVirtual,
					commands, _nextFrame);
				return;
			case ChannelTargetKind.Global:
				long? previousGlobalOwner = _activePatternCursorId;
				_activePatternCursorId = invocationId;
				try
				{
					ApplyLiveEvent(target, commands);
				}
				finally
				{
					_activePatternCursorId = previousGlobalOwner;
				}
				return;
			case ChannelTargetKind.Physical:
				// Physical owner 0 is shared by independent root Patterns.
				// Record the exact originating cursor at StartNote, so
				// cancellation can later cut only its current/NNA voices.
				long? priorCursor = _activePatternCursorId;
				_activePatternCursorId = invocationId;
				try
				{
					ApplyEvent(new NoteEvent(
						new MusicalTime(FrameTime.FrameStartTime(
							_nextFrame, _context.Configuration.SampleRate), 0),
						target, commands)
					{
						PhysicalPlaybackOwner = physicalPlaybackOwner,
					}, _nextFrame);
				}
				finally
				{
					_activePatternCursorId = priorCursor;
				}
				return;
			default:
				ApplyLiveEvent(target, commands);
				return;
		}
	}

	/// <summary>
	/// Cancel audible voices created by this Pattern cursor, both
	/// physical and scoped virtual (including displaced NNA). Physical
	/// owner zero is shared by top-level Patterns; the originating
	/// cursor identity prevents canceling their surviving sibling notes.
	/// Natural Pattern completion does not call this: notes may tail out.
	/// </summary>
	public void CancelScopedVoices(long invocationId)
	{
		if (invocationId < 0)
			throw new ArgumentOutOfRangeException(nameof(invocationId));
		for (int i = _nativeGlobal.Count - 1; i >= 0; i--)
		{
			ActiveParametricEffect active = _nativeGlobal[i];
			if (active.OwnerId != invocationId)
				continue;
			_globalOperators.Remove(active.Operator);
			_nativeGlobal.RemoveAt(i);
		}
		foreach (PlaybackChannelState channel in _channels.Values)
		{
			channel.CancelNativeParametric(invocationId);
			channel.CurrentVoice?.CancelNativeParametric(
				invocationId, _nextFrame);
			// A different invocation may still own the current physical voice.
			// Remove canceled operator contributions from its live channel state
			// immediately, even when no further continuous operator remains.
			channel.SynchronizeContinuousState(_nextFrame);
		}
		foreach (PlaybackChannelState channel in _channels.Values)
			if (channel.CurrentVoice?.OriginPatternCursorId == invocationId)
				channel.CutCurrentVoice();
		foreach (var pair in _scopedVirtualChannels)
			if (pair.Key.Owner == invocationId)
				pair.Value.CutCurrentVoice();
		// NNA Continue/Off/Fade moves the previous scoped virtual voice
		// into the global displaced-voice pool. Cursor cancellation still
		// owns those voices; it must not accidentally cut other cursors'
		// identical virtual IDs or unrelated physical NNA voices.
		for (int i = _virtualVoices.Count - 1; i >= 0; i--)
		{
			PlaybackVoice voice = _virtualVoices[i];
			if (voice.OriginScopedVirtualOwner == invocationId)
			{
				voice.AddCutTo(GetScopedChannel(
					invocationId, voice.OriginVirtualChannelId!.Value).AntiClickTail);
				RetireDisplacedVoiceAt(i);
			}
			else if (voice.OriginPatternCursorId == invocationId)
			{
				voice.AddCutTo(GetChannelState(
					voice.OriginPhysicalChannel,
					voice.OriginPhysicalPlaybackOwner).AntiClickTail);
				RetireDisplacedVoiceAt(i);
			}
		}
	}

	private PlaybackChannelState GetScopedChannel(long owner, uint id)
	{
		if (_scopedVirtualChannels.TryGetValue(
			(owner, id), out PlaybackChannelState? channel))
			return channel;
		channel = new PlaybackChannelState(
			_context.Configuration.OutputChannelCount,
			_context.Configuration.SampleRate, _tickClock,
			unchecked(0x5649525455414C00UL
				+ (ulong)id * 0x9E3779B97F4A7C15UL
				+ (ulong)owner));
		_scopedVirtualChannels.Add((owner, id), channel);
		return channel;
	}

	private void ApplyVirtualBroadcast(
		long owner, bool allOwners,
		IReadOnlyList<NoteCommand> commands, long frame)
	{
		// Snapshot the eligible channels before applying commands.
		// Same-frame starts are not pre-existing voices, even if their
		// start event was dequeued earlier in this callback.
		List<PlaybackChannelState> eligible = [];
		foreach (var pair in _scopedVirtualChannels)
			if ((allOwners || pair.Key.Owner == owner)
				&& pair.Value.CurrentVoice is PlaybackVoice voice
				&& voice.StartFrame < frame)
				eligible.Add(pair.Value);
		if (allOwners)
			foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
				if (channel.CurrentVoice is PlaybackVoice voice
					&& voice.StartFrame < frame)
					eligible.Add(channel);
		foreach (PlaybackChannelState channel in eligible)
			foreach (NoteCommand command in commands)
			{
				// Broadcasts control existing voices, not the creation
				// of new ones or independent target memory.
				switch (command)
				{
					case NoteOffCommand:
						channel.CurrentVoice?.ApplyNoteOff(
							frame, _context.Configuration.SampleRate);
						CullFinishedCurrentVoice(channel, frame);
						break;
					case NoteCutCommand:
						channel.CutCurrentVoice();
						break;
					case SetNoteVolumeCommand volume:
						channel.SetNoteVolume(volume.Volume);
						break;
					default:
						throw new NotSupportedException(
							$"Broadcast command {command.GetType().Name} is not supported.");
				}
			}

		// AllVirtual also includes displaced NNA voices, which already
		// live in the session-wide virtual pool. They have no invocation
		// scope: AllVirtualInScope must not control them.
		if (!allOwners)
			return;
		for (int i = _virtualVoices.Count - 1; i >= 0; i--)
		{
			PlaybackVoice voice = _virtualVoices[i];
			if (voice.StartFrame >= frame)
				continue;
			bool cut = false;
			foreach (NoteCommand command in commands)
			{
				switch (command)
				{
					case NoteOffCommand:
						voice.ApplyNoteOff(frame,
							_context.Configuration.SampleRate);
						break;
					case NoteCutCommand:
						cut = true;
						break;
					case SetNoteVolumeCommand volume:
						voice.SetNoteVolume(volume.Volume);
						break;
					default:
						throw new NotSupportedException(
							$"Broadcast command {command.GetType().Name} is not supported.");
				}
			}
			if (cut)
			{
				voice.AddCutTo(GetChannelState(
					voice.OriginPhysicalChannel,
					voice.OriginPhysicalPlaybackOwner).AntiClickTail);
				RetireDisplacedVoiceAt(i);
			}
		}
	}

	public void ApplyLiveEvent(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ArgumentNullException.ThrowIfNull(commands);

		if (target.Kind == ChannelTargetKind.Virtual)
		{
			ApplyVirtualEvent(
				target.VirtualChannelId,
				commands,
				_nextFrame,
				cutIndefiniteAfterNoteOff: true);
			return;
		}

		TimeSpan eventTime =
			FrameTime.FrameStartTime(
				_nextFrame,
				_context.Configuration.SampleRate);
		ApplyEvent(
			new NoteEvent(
				new MusicalTime(
					eventTime,
					0.0),
				target,
				commands),
			_nextFrame);
	}

	/// <summary>
	/// Fills destination with the next sequential block of interleaved float PCM.
	/// The start frame must equal NextFrame; seeking will be layered separately.
	/// </summary>
	public void Render(
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		if (startFrame != _nextFrame)
		{
			throw new InvalidOperationException(
				$"PlaybackSession is sequential. Expected start frame {_nextFrame}, received {startFrame}.");
		}
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		int outputChannelCount = _context.Configuration.OutputChannelCount;
		int requiredSamples = checked(frameCount * outputChannelCount);
		if (destination.Length != requiredSamples)
		{
			throw new ArgumentException(
				"Destination length must exactly match frame count and output channel count.",
				nameof(destination));
		}

		destination.Clear();

		long blockEnd = checked(startFrame + frameCount);
		long cursor = startFrame;

		while (cursor < blockEnd)
		{
			ProcessEventsThrough(cursor);

			long nextEventFrame = GetNextEventFrame();
			long segmentEnd = nextEventFrame <= cursor
				? cursor
				: Math.Min(blockEnd, nextEventFrame);

			if (segmentEnd == cursor)
				continue;

			int segmentFrames = checked((int)(segmentEnd - cursor));
			int destinationFrameOffset = checked((int)(cursor - startFrame));
			Span<float> segmentDestination = destination.Slice(
				checked(destinationFrameOffset * outputChannelCount),
				checked(segmentFrames * outputChannelCount));

			RenderSegment(cursor, segmentFrames, segmentDestination);
			cursor = segmentEnd;
		}

		// Events exactly at the end boundary affect the next frame and therefore
		// are intentionally left for the next Render call.
		SynchronizeTempoRamp(blockEnd);
		// The speaker filters own continuous state across render blocks.
		// Apply *once* after all channels, virtual/NNA voices and master
		// volume have been mixed; not inside individual voice renderers.
		_outputSpeakerFilters?.Apply(destination, outputChannelCount);
		_nextFrame = blockEnd;
	}

	private void ProcessEventsThrough(long frame)
	{
		while (_nextEventIndex < _schedule.Count)
		{
			NoteEvent noteEvent = _schedule[_nextEventIndex];
			long eventFrame = FrameTime.Ceiling(
				noteEvent.Offset.TimeOffset,
				_context.Configuration.SampleRate);

			if (eventFrame > frame)
				break;

			ApplyEvent(noteEvent, Math.Max(eventFrame, 0));
			_nextEventIndex++;
		}
	}

	private long GetNextEventFrame()
	{
		if (_nextEventIndex >= _schedule.Count)
			return long.MaxValue;

		return FrameTime.Ceiling(
			_schedule[_nextEventIndex].Offset.TimeOffset,
			_context.Configuration.SampleRate);
	}

	private void ApplyEvent(NoteEvent noteEvent, long eventFrame)
	{
		SynchronizeTempoRamp(eventFrame);

		if (noteEvent.Target.Kind == ChannelTargetKind.Global)
		{
			foreach (NoteCommand command in noteEvent.Commands)
				ApplyGlobalCommand(command, eventFrame);
			return;
		}

		if (noteEvent.Target.Kind == ChannelTargetKind.Virtual)
		{
			ApplyVirtualEvent(
				noteEvent.Target.VirtualChannelId,
				noteEvent.Commands,
				eventFrame,
				cutIndefiniteAfterNoteOff: false);
			return;
		}

		if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
		{
			throw new NotSupportedException(
				$"Playback target {noteEvent.Target.Kind} is not implemented yet.");
		}

		int physicalChannel = noteEvent.Target.PhysicalChannel;
		PlaybackChannelState channel = GetChannelState(
			physicalChannel, noteEvent.PhysicalPlaybackOwner);

		foreach (NoteCommand command in noteEvent.Commands)
		{
			ApplyCommand(
				physicalChannel,
				channel,
				command,
				eventFrame,
				noteEvent.Offset.TimeOffset,
				noteEvent.PhysicalPlaybackOwner);
		}
	}

	private void ApplyVirtualEvent(
		uint channelId,
		IReadOnlyList<NoteCommand> commands,
		long eventFrame,
		bool cutIndefiniteAfterNoteOff)
		=> ApplyVirtualCommands(GetVirtualChannelState(channelId),
			commands, eventFrame, cutIndefiniteAfterNoteOff,
			virtualChannelId: channelId);

	private void ApplyVirtualCommands(
		PlaybackChannelState channel,
		IReadOnlyList<NoteCommand> commands,
		long eventFrame,
		bool cutIndefiniteAfterNoteOff,
		uint? virtualChannelId = null,
		long? scopedOwner = null)
	{
		foreach (NoteCommand command in commands)
		{
			switch (command)
			{
				case StartNoteCommand start:
					StartVirtualNote(
						channel,
						start,
						eventFrame,
						virtualChannelId,
						scopedOwner);
					break;

				case NoteOffCommand:
					if (channel.CurrentVoice is PlaybackVoice voice)
					{
						voice.ApplyNoteOff(
							eventFrame,
							_context.Configuration.SampleRate);

						if (cutIndefiniteAfterNoteOff
							&& !GetEffectiveVoiceEndFrameExclusive(
								voice,
								eventFrame).HasValue)
						{
							channel.CutCurrentVoice();
						}
						else
						{
							CullFinishedCurrentVoice(
								channel,
								eventFrame);
						}
					}
					break;

				case NoteCutCommand:
					channel.CutCurrentVoice();
					break;
				case SetNoteVolumeCommand volume:
					channel.SetNoteVolume(volume.Volume);
					break;

				case SetNoteFilterCommand:
				case SetNoteFilterParametersCommand:
				case SetNoteFilterAssignmentsCommand:
					ApplyCurrentNoteFilterCommand(channel, command);
					break;

				case SetNoteFilterParameterEnvelopeCommand envelope:
					ApplyCurrentNoteFilterEnvelopeCommand(channel, envelope, frame);
					break;

				case SetChannelFilterCommand:
				case SetChannelFilterParametersCommand:
				case SetChannelFilterAssignmentsCommand:
					ApplyHostFilterCommand(command, null, virtualChannelId, scopedOwner);
					break;

				case SetSourceFrameOffsetCommand offset:
					ApplyNativeSourceFrameOffset(
						channel, offset, "Script virtual native offset");
					break;

				case SetCurrentVoiceDisplacementActionCommand displacement:
					if (channel.CurrentVoice is not null)
						channel.CurrentVoice.SetNewNoteActionOverride(
							displacement.Action switch
							{
								NoteDisplacementAction.Cut => NewNoteAction.Cut,
								NoteDisplacementAction.Continue => NewNoteAction.Continue,
								NoteDisplacementAction.Off => NewNoteAction.Off,
								NoteDisplacementAction.Fade => NewNoteAction.Fade,
								_ => throw new InvalidOperationException(
									$"Unsupported virtual displacement {displacement.Action}."),
							});
					break;

				case ApplyPastNoteActionCommand past:
					if (virtualChannelId.HasValue)
						ApplyPastVirtualNoteAction(
							channel, virtualChannelId.Value, scopedOwner,
							past.Action, eventFrame);
					break;

				default:
					throw new NotSupportedException(
						$"Render command {command.GetType().Name} is not supported on an explicitly targeted virtual channel.");
			}
		}
	}

	/// <summary>S7x on a virtual channel applies only to its own
	/// NNA-migrated notes, including the original cursor's scope. It
	/// must not touch the replacement voice or a sibling's same ID.</summary>
	/// <summary>
	/// Apply native source-frame seeking to whichever voice is current at the
	/// event deadline, whether hosted on a physical or scoped virtual channel.
	/// </summary>
	private void ApplyNativeSourceFrameOffset(
		PlaybackChannelState channel,
		SetSourceFrameOffsetCommand offset,
		string diagnosticOperation)
	{
		if (channel.CurrentVoice is not PlaybackVoice voice
			|| voice.Sound is not ISourceFrameSeekableSound seekable)
			return;
		seekable.SetSourceFrameOffset(
			voice.SoundState, offset.SourceFrameOffset);
		if (seekable.SeekCost == SourceFrameSeekCost.ReplayRequired
			&& offset.SourceFrameOffset > 0)
		{
			ReplayRequiredSeekObserved?.Invoke(
				diagnosticOperation, offset.SourceFrameOffset);
		}
	}

	private void ApplyPastVirtualNoteAction(
		PlaybackChannelState channel, uint virtualChannelId,
		long? scopedOwner, TrackerPastNoteAction action, long frame)
	{
		for (int i = _virtualVoices.Count - 1; i >= 0; i--)
		{
			PlaybackVoice voice = _virtualVoices[i];
			if (voice.OriginVirtualChannelId != virtualChannelId
				|| voice.OriginScopedVirtualOwner != scopedOwner)
				continue;
			switch (action)
			{
				case TrackerPastNoteAction.Cut:
					voice.AddCutTo(channel.AntiClickTail);
					RetireDisplacedVoiceAt(i);
					break;
				case TrackerPastNoteAction.Off:
					voice.ApplyNoteOff(frame, SampleRate);
					break;
				case TrackerPastNoteAction.Fade:
					voice.RequestNoteFade(frame, SampleRate);
					break;
				default:
					throw new InvalidOperationException(
						$"Unsupported virtual past-note action {action}.");
			}
		}
	}

	private void StartVirtualNote(
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame,
		uint? virtualChannelId,
		long? scopedOwner)
	{
		// Virtual channels have the same per-note NNA displacement as
		// physical channels. Previous voices retain their original scope
		// after migration to the displaced-voice pool.
		DisplaceCurrentVoice(channel, eventFrame);

		if (!_soundResolver.TryResolve(
			start.SourceId,
			start.Mixdown,
			out ISound? sound)
			|| sound is null)
		{
			return;
		}

		double inheritedPitch = ReadInheritedPitchAt(eventFrame);
		SoundInvocation? invocation =
			sound.CreateInvocation(
				checked(start.PitchMultiplier * inheritedPitch),
				start.PlaybackSpeedMultiplier);
		if (invocation is null)
			return;

		if (start.Volume.HasValue)
			channel.SetNoteVolume(start.Volume.Value);

		// Virtual voices inside a flattened Pattern have the same
		// enclosing logical source-note gain ancestry as physical voices.
		List<FlattenedSourceVolume> ancestors = [];
		if (start.ParentSourceScopes is { } scopes)
			foreach (long scope in scopes)
				if (_sourceVolumes.TryGetValue(scope,
					out FlattenedSourceVolume? controller))
					ancestors.Add(controller);
		List<PlaybackChannelState> enclosingVolumes = [];
		if (start.ParentOverallChannels is { } parents)
			foreach (ParentVolumeChannel parent in parents)
				enclosingVolumes.Add(GetChannelState(parent.Host, parent.Owner));
		PlaybackVoice voice = new(
			invocation.Sound,
			invocation.State,
			invocation.Configuration,
			eventFrame,
			_context.Configuration.OutputChannelCount,
			_context.Configuration.SampleRate,
			channel.FilterParameters,
			channel.NoteVolume,
			channel.OverallVolume,
			_tickClock,
			_nextVoiceModulationSeed++,
			originPhysicalChannel: 0,
			sourceGainMultiplier: start.GainMultiplier,
			enclosingVolumeChannels: enclosingVolumes,
			enclosingSourceVolumes: ancestors,
			inheritedPitch: InheritedPitchAtFrame,
			inheritedPitchAtStart: inheritedPitch,
			initialNotePitchMultiplier: start.PitchMultiplier,
			toneFilters: invocation.ToneFilters,
			eventFilter: start.EventFilterConfiguration,
			enclosingEventFilters: BuildEnclosingEventFilters(ancestors))
		{
			OriginVirtualChannelId = virtualChannelId,
			OriginScopedVirtualOwner = scopedOwner,
		};

		channel.AttachVoice(
			voice,
			eventFrame);
	}

	private void ApplyCommand(
		int physicalChannel,
		PlaybackChannelState channel,
		NoteCommand command,
		long eventFrame,
		TimeSpan eventTime,
		long physicalPlaybackOwner)
	{
		switch (command)
		{
			case BeginFlattenedSourceVolumeCommand begin:
				BeginFlattenedSource(channel, begin, eventFrame);
				break;

			case ControlFlattenedSourceCommand control:
				ApplyFlattenedScopeAction(
					control.ChildScopeId, control.Action);
				break;

			case StartNoteCommand start:
				StartNote(
					physicalChannel,
					channel,
					start,
					eventFrame,
					physicalPlaybackOwner);
				break;

			case SetNoteFilterCommand:
			case SetNoteFilterParametersCommand:
			case SetNoteFilterAssignmentsCommand:
				ApplyCurrentNoteFilterCommand(channel, command);
				break;

			case SetNoteFilterParameterEnvelopeCommand envelope:
				ApplyCurrentNoteFilterEnvelopeCommand(channel, envelope, eventFrame);
				break;

			case SetChannelFilterCommand:
			case SetChannelFilterParametersCommand:
			case SetChannelFilterAssignmentsCommand:
				// Always target the *real physical host* regardless of
				// PhysicalPlaybackOwner/flattened logical note scope.
				ApplyHostFilterCommand(command, physicalChannel, null, null);
				break;

			case NoteCutCommand:
				channel.CutCurrentVoice();
				break;

			case NoteOffCommand:
				channel.CurrentVoice?.ApplyNoteOff(
					eventFrame,
					_context.Configuration.SampleRate);
				CullFinishedCurrentVoice(channel, eventFrame);
				break;

			case SetEnvelopeEnabledCommand envelope:
				channel.CurrentVoice?.SetEnvelopeEnabled(
					envelope.Target,
					eventFrame,
					envelope.Enabled);
				break;

			case SetGlobalVolumeCommand volume:
				SetGlobalVolume(eventFrame, volume.Volume);
				break;

			case AdjustGlobalVolumeCommand adjust:
				AdjustGlobalVolume(
					eventFrame,
					adjust.TrackerUnits);
				break;

			case SetGlobalVolumeSlideCommand slide:
				SetGlobalVolumeSlide(
					physicalChannel,
					eventFrame,
					slide.TicksPerRow ?? _speed,
					slide.TrackerUnitsPerTick,
					physicalPlaybackOwner);
				break;

			case ClearGlobalVolumeSlideCommand:
				ClearGlobalVolumeSlide(
					physicalChannel,
					eventFrame,
					physicalPlaybackOwner);
				break;

			case SetNoteVolumeCommand volume:
				if (channel.CurrentFlattenedSource is { } source)
				{
					source.Set(eventFrame, volume.Volume);
					channel.CaptureCurrentNoteVolume(volume.Volume);
				}
				else
					channel.SetNoteVolume(volume.Volume);
				break;

			case RememberFlatteningNoteVolumeCommand remembered:
				// Starting a flattened collection is a logical note start,
				// not a modification of an older live physical voice.
				channel.CaptureCurrentNoteVolume(remembered.Volume);
				break;

			case StartParametricEffectCommand parametric:
				if (parametric.Parameter is
					ParametricEffectParameter.NoteVolume or
					ParametricEffectParameter.PitchLinearUnits)
					channel.CurrentVoice?.BeginNativeParametric(parametric, eventFrame,
						_activePatternCursorId);
				else
					channel.BeginParametric(parametric, eventFrame, _activePatternCursorId);
				break;

			case SetOverallChannelVolumeCommand volume:
				channel.SetOverallVolume(
					eventFrame,
					volume.Volume);
				break;

			case AdjustOverallChannelVolumeCommand adjust:
				channel.AdjustOverallVolume(
					eventFrame,
					adjust.TrackerUnits);
				break;

			case SetOverallChannelVolumeSlideCommand slide:
				channel.SetOverallVolumeSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.TrackerUnitsPerTick);
				break;

			case ClearOverallChannelVolumeSlideCommand:
				channel.ClearOverallVolumeSlide(eventFrame);
				break;

			case SetSurroundCommand surround:
				channel.SetSurround(eventFrame, surround.Enabled);
				break;

			case SetSpatialPositionCommand position:
				channel.SetPosition(
					eventFrame,
					position.Position);
				break;

			case AdjustSpatialXCommand adjust:
				channel.AdjustSpatialX(
					eventFrame,
					adjust.DeltaX,
					adjust.MinimumX,
					adjust.MaximumX);
				break;

			case SetSpatialXSlideCommand slide:
				channel.SetSpatialXSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.SpatialUnitsPerTick,
					slide.MinimumX,
					slide.MaximumX);
				break;

			case ClearSpatialXSlideCommand:
				channel.ClearSpatialXSlide(eventFrame);
				break;

			case SetResonantFilterCommand filter:
				channel.SetFilterParameters(
					eventFrame,
					new ResonantFilterParameters(
						filter.Cutoff,
						filter.Resonance));
				break;

			case SetResonantFilterCutoffCommand filter:
				channel.SetFilterCutoff(
					eventFrame,
					filter.Cutoff);
				break;

			case SetResonantFilterResonanceCommand filter:
				channel.SetFilterResonance(
					eventFrame,
					filter.Resonance);
				break;

			case SetPlaybackFrequencyCommand frequency:
				channel.CurrentVoice?.SetPlaybackFrequency(
					eventFrame, frequency.Frequency,
					frequency.LocalPitchContextMultiplier);
				break;

			case SetPlaybackOffsetCommand playbackOffset:
				if (channel.CurrentVoice is not null)
					channel.CurrentVoice.SoundState.PlaybackOffset = playbackOffset.Offset;
				break;

			case SetSourceFrameOffsetCommand sourceFrameOffset:
				ApplyNativeSourceFrameOffset(
					channel, sourceFrameOffset, "Oxx native offset");
				break;

			case AdjustPitchLinearUnitsCommand adjust:
				channel.CurrentVoice?.AdjustPitchLinearUnits(
					eventFrame,
					adjust.LinearUnits);
				break;

			case AdjustCurrentNoteVolumeCommand adjust:
				if (channel.CurrentFlattenedSource is { } currentVolumeSource)
				{
					channel.CaptureCurrentNoteVolume(
						currentVolumeSource.Adjust(eventFrame, adjust.TrackerUnits));
				}
				else if (channel.CurrentVoice is not null)
				{
					double volume =
						channel.CurrentVoice.AdjustNoteVolume(
							eventFrame,
							adjust.TrackerUnits);
					channel.CaptureCurrentNoteVolume(volume);
				}
				break;

			case AdjustNoteVolumeCommand adjust:
				if (channel.CurrentFlattenedSource is { } adjustedVolumeSource)
				{
					channel.CaptureCurrentNoteVolume(
						adjustedVolumeSource.Adjust(eventFrame, adjust.TrackerUnits));
				}
				else if (channel.CurrentVoice is not null)
				{
					double volume =
						channel.CurrentVoice.AdjustNoteVolume(
							eventFrame,
							adjust.TrackerUnits);
					channel.CaptureCurrentNoteVolume(volume);
				}
				else
				{
					channel.SetNoteVolume(
						Math.Clamp(
							channel.NoteVolume
								+ adjust.TrackerUnits / 64.0,
							0.0,
							1.0));
				}
				break;

			case SetTonePortamentoCommand tonePortamento:
				if (channel.CurrentVoice is null
					&& tonePortamento.TargetNote is not null)
				{
					StartNote(
						physicalChannel,
						channel,
						tonePortamento.TargetNote,
						eventFrame,
						physicalPlaybackOwner);
				}

				channel.CurrentVoice?.SetTonePortamento(
					eventFrame,
					_tempo,
					tonePortamento.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					tonePortamento.LinearUnitsPerTick,
					tonePortamento.TargetNote?.PitchMultiplier,
					tonePortamento.Glissando);
				break;

			case ClearTonePortamentoCommand:
				channel.CurrentVoice?.ClearTonePortamento(eventFrame);
				break;

			case SetPitchSlideCommand slide:
				channel.CurrentVoice?.SetPitchSlide(
					eventFrame,
					_tempo,
					slide.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					slide.LinearUnitsPerTick);
				break;

			case ClearPitchSlideCommand:
				channel.CurrentVoice?.ClearPitchSlide(eventFrame);
				break;

			case SetNoteVolumeSlideCommand slide:
				if (channel.CurrentFlattenedSource is { } slidingVolumeSource)
					slidingVolumeSource.Slide(eventFrame,
						slide.TicksPerRow ?? _speed, slide.TrackerUnitsPerTick);
				else
					channel.CurrentVoice?.SetNoteVolumeSlide(
						eventFrame,
						_tempo,
						slide.TicksPerRow ?? _speed,
						_context.Configuration.SampleRate,
						slide.TrackerUnitsPerTick);
				break;

			case ClearNoteVolumeSlideCommand:
				if (channel.CurrentFlattenedSource is { } clearedVolumeSource)
					channel.CaptureCurrentNoteVolume(clearedVolumeSource.Clear(eventFrame));
				else if (channel.CurrentVoice is not null)
				{
					double volume =
						channel.CurrentVoice.ClearNoteVolumeSlide(eventFrame);
					channel.SetNoteVolume(volume);
				}
				break;

			case SetTremorCommand tremor:
				channel.SetTremor(
					eventFrame,
					_tempo,
					tremor.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					tremor.OnTicks,
					tremor.OffTicks);
				break;

			case ClearTremorCommand:
				channel.ClearTremor();
				break;

			case SetPanbrelloWaveformCommand waveform:
				channel.SetPanbrelloWaveform(
					eventFrame,
					waveform.Waveform);
				break;

			case SetPanbrelloCommand panbrello:
				channel.SetPanbrello(
					eventFrame,
					_tempo,
					panbrello.TicksPerRow ?? _speed,
					_context.Configuration.SampleRate,
					panbrello.Speed,
					panbrello.Depth,
					panbrello.Waveform);
				break;

			case ClearPanbrelloCommand:
				channel.ClearPanbrello(eventFrame);
				break;

			case RetriggerCurrentVoiceCommand retrigger:
				if (channel.CurrentVoice is not null)
				{
					PlaybackVoice voice = channel.CurrentVoice;
					voice.AddCutTo(channel.AntiClickTail);

					double volume = TrackerRetrigger.ApplyVolumeTransform(
						voice.GetBaseNoteVolume(eventFrame),
						retrigger.VolumeTransform);
					channel.SetNoteVolume(volume);

					voice.Retrigger(eventFrame);
					if (voice.Sound is ISourceFrameSeekableSound retriggerSeekable
						&& retriggerSeekable.SeekCost == SourceFrameSeekCost.ReplayRequired)
						ReplayRequiredSeekObserved?.Invoke("Qxy retrigger", 0);
				}
				break;

			case SetArpeggioCommand arpeggio:
				channel.CurrentVoice?.SetArpeggio(
					eventFrame,
					_tempo,
					_context.Configuration.SampleRate,
					arpeggio.FirstSemitones,
					arpeggio.SecondSemitones);
				break;

			case ClearArpeggioCommand:
				channel.CurrentVoice?.ClearArpeggio(eventFrame);
				break;

			case SetTremoloCommand tremolo:
				channel.CurrentVoice?.SetTremolo(
					eventFrame,
					eventTime,
					_tempo,
					_context.Configuration.SampleRate,
					tremolo.Speed,
					tremolo.Depth,
					tremolo.Waveform);
				break;

			case ClearTremoloCommand:
				channel.CurrentVoice?.ClearTremolo(
					eventFrame,
					eventTime);
				break;

			case SetVibratoCommand vibrato:
				channel.CurrentVoice?.SetVibrato(
					eventFrame,
					eventTime,
					_tempo,
					_context.Configuration.SampleRate,
					vibrato.Speed,
					vibrato.Depth,
					vibrato.Waveform,
					vibrato.DepthScale);
				break;

			case ClearPitchModulationCommand:
				channel.CurrentVoice?.ClearPitchModulation(
					eventFrame,
					eventTime);
				break;

			case ApplyPastNoteActionCommand pastNote:
				ApplyPastNoteAction(
					physicalChannel,
					channel,
					pastNote.Action,
					eventFrame,
					physicalPlaybackOwner);
				break;

			case SetCurrentVoiceDisplacementActionCommand displacement:
				if (channel.CurrentVoice is not null)
				{
					NewNoteAction action =
						displacement.Action switch
						{
							NoteDisplacementAction.Cut =>
								NewNoteAction.Cut,
							NoteDisplacementAction.Continue =>
								NewNoteAction.Continue,
							NoteDisplacementAction.Off =>
								NewNoteAction.Off,
							NoteDisplacementAction.Fade =>
								NewNoteAction.Fade,
							_ => throw new InvalidOperationException(
								$"Unsupported displacement action {displacement.Action}."),
						};

					channel.CurrentVoice.SetNewNoteActionOverride(action);
				}
				break;

			case SetTempoCommand tempo:
				SetBaselineTempo(
					eventFrame,
					tempo.TicksPerDiachron);
				break;

			case SetTempoRampCommand ramp:
				SetTempoRamp(
					eventFrame,
					ramp.EndingTempo,
					ramp.TrackerTicks);
				break;
			case SetSpeedCommand:
				// PatternNoteProcessor has already baked speed into event timing.
				break;

			default:
				throw new NotSupportedException(
					$"Render command {command.GetType().Name} is not implemented yet.");
		}
	}

	private void ApplyGlobalCommand(
		NoteCommand command,
		long eventFrame)
	{
		switch (command)
		{
			case StartParametricEffectCommand effect:
				if (effect.Parameter != ParametricEffectParameter.GlobalVolume)
					throw new NotSupportedException(
						"Channel parametric effect needs a physical target.");
				ActiveParametricEffect active = new(
					effect, eventFrame, _context.Configuration.SampleRate, _tickClock)
					{ OwnerId = _activePatternCursorId };
				_globalOperators.Add(active.Operator);
				_nativeGlobal.Add(active);
				break;

			case SetTempoCommand tempo:
				SetBaselineTempo(
					eventFrame,
					tempo.TicksPerDiachron);
				break;

			case SetTempoRampCommand ramp:
				SetTempoRamp(
					eventFrame,
					ramp.EndingTempo,
					ramp.TrackerTicks);
				break;
			case SetSpeedCommand speed:
				_speed = speed.TicksPerRow;
				break;

			case SetGlobalVolumeCommand volume:
				SetGlobalVolume(eventFrame, volume.Volume);
				break;
		}
	}


	/// <summary>Mutate the sole current logical note, not a channel-memory
	/// slot and never displaced NNA or previous released notes. A flattened
	/// instigator has many descendants but one controlling scope.</summary>
	/// <summary>Apply one selection or atomic parameter batch to the
	/// actual output host. The per-owner playback-channel memory is *not*
	/// the channel Filter identity; child cancellation has no rollback.</summary>
	private void ApplyHostFilterCommand(NoteCommand command,
		int? physicalHost, uint? virtualId, long? scopedOwner)
	{
		try
		{
			if (physicalHost is null && virtualId is null)
				throw new ArgumentException("No real physical or virtual host target.");
			ChannelOutputFilterState? previous;
			if (physicalHost is int host)
				_physicalOutputFilters.TryGetValue(host, out previous);
			else if (scopedOwner is long owner)
				_scopedVirtualOutputFilters.TryGetValue(
					(owner, virtualId!.Value), out previous);
			else
				_targetedVirtualOutputFilters.TryGetValue(virtualId!.Value, out previous);

			ChannelOutputFilterState? prepared;
			switch (command)
			{
				case SetChannelFilterCommand selection:
					FilterInstance? selectedUse = selection.Configuration;
					if (selection.FilterDefinitionId is ObjectId selected
						&& (selected.IsNone
							|| selectedUse is null
							|| selectedUse.DefinitionId != selected))
						throw new ArgumentException(
							"Selected channel Filter ID is unavailable or mismatched.");
					// Every explicit select starts a *fresh* DSP history;
					// clearing discards only this host's state.
					prepared = selectedUse is null ? null
						: new ChannelOutputFilterState(selectedUse,
							SampleRate, OutputChannelCount);
					break;
				case SetChannelFilterParametersCommand update:
					if (previous is null)
						throw new InvalidOperationException(
							"No channel-output Filter selected on the target host.");
					prepared = previous.PrepareParameterUpdate(
						update.StageId, update.Assignments);
					break;
				case SetChannelFilterAssignmentsCommand text:
					if (previous is null)
						throw new InvalidOperationException(
							"No channel-output Filter selected for assignments.");
					prepared = previous.PrepareTextUpdate(text.Text);
					break;
				default:
					throw new InvalidOperationException(
						"Unexpected channel Filter command.");
			}
			if (physicalHost is int physical)
			{
				if (prepared is null) _physicalOutputFilters.Remove(physical);
				else _physicalOutputFilters[physical] = prepared;
			}
			else if (scopedOwner is long scope)
			{
				if (prepared is null)
					_scopedVirtualOutputFilters.Remove((scope, virtualId!.Value));
				else _scopedVirtualOutputFilters[(scope, virtualId!.Value)] = prepared;
			}
			else
			{
				if (prepared is null) _targetedVirtualOutputFilters.Remove(virtualId!.Value);
				else _targetedVirtualOutputFilters[virtualId!.Value] = prepared;
			}
		}
		catch (Exception failure) when (failure is ArgumentException
			or InvalidOperationException or KeyNotFoundException or OverflowException)
		{
			ChannelFilterCommandRejected?.Invoke(failure.Message);
		}
	}

	private void ApplyCurrentNoteFilterEnvelopeCommand(
		PlaybackChannelState channel,
		SetNoteFilterParameterEnvelopeCommand command, long frame)
	{
		if (channel.CurrentFlattenedSource is not null)
		{
			NoteFilterCommandRejected?.Invoke(
				"Flattened note Filter envelope scope requires descendant binding support.");
			return;
		}
		if (channel.CurrentVoice is not PlaybackVoice voice)
		{
			NoteFilterCommandRejected?.Invoke(
				"No current note for Filter parameter envelope.");
			return;
		}
		try
		{
			IEnvelopeCurve? curve = null;
			if (command.EnvelopeId is ObjectId id)
			{
				if (id.IsNone || NoteFilterEnvelopeResolver is null
					|| !NoteFilterEnvelopeResolver.TryResolve(id, out curve)
					|| curve is null)
					throw new ArgumentException(
						$"Unknown or unavailable Filter Envelope {id.Value}.");
			}
			voice.SetEventFilterParameterEnvelope(
				command.StageId, command.ParameterName, curve, frame);
		}
		catch (Exception error) when (error is ArgumentException
			or InvalidOperationException or KeyNotFoundException
			or OverflowException)
		{
			NoteFilterCommandRejected?.Invoke(error.Message);
		}
	}

	private void ApplyCurrentNoteFilterCommand(
		PlaybackChannelState channel, NoteCommand command)
	{
		FlattenedSourceVolume? controller = channel.CurrentFlattenedSource;
		PlaybackVoice? current = controller is null ? channel.CurrentVoice : null;
		if (controller is null && current is null)
		{
			NoteFilterCommandRejected?.Invoke("no current note on target channel");
			return;
		}
		try
		{
			FilterInstance? previous = controller is null
				? current!.GetEventFilterConfiguration()
				: controller.EventFilterConfiguration?.Clone();
			FilterInstance? candidate;
			switch (command)
			{
				case SetNoteFilterCommand selection:
					if (selection.FilterDefinitionId.HasValue
						&& (selection.Configuration is null
							|| selection.Configuration.DefinitionId
								!= selection.FilterDefinitionId.Value))
					{
						NoteFilterCommandRejected?.Invoke(
							"selected Filter ID is unavailable or mismatched");
						return;
					}
					candidate = selection.Configuration?.Clone();
					break;
				case SetNoteFilterParametersCommand parameters:
					if (previous is null)
					{
						NoteFilterCommandRejected?.Invoke(
							"no event Filter selected for current note");
						return;
					}
					candidate = previous.Clone();
					candidate.SetStageParameters(parameters.StageId,
						parameters.Assignments);
					break;
				case SetNoteFilterAssignmentsCommand text:
					if (previous is null)
					{
						NoteFilterCommandRejected?.Invoke(
							"no event Filter selected for assignments");
						return;
					}
					candidate = FilterAssignmentText.ApplyToClone(previous, text.Text);
					break;
				default:
					throw new InvalidOperationException(
						"Unexpected note Filter command.");
			}
			if (controller is null)
			{
				var prepared = current!.PrepareEventFilterUpdate(candidate);
				current.PublishEventFilterUpdate(
					prepared.Configuration, prepared.State);
				if (command is SetNoteFilterCommand)
					current.ClearEventFilterParameterEnvelopes();
				return;
			}

			// All descendants, including existing voices displaced by NNA,
			// must be prepared before publishing the logical scope change.
			// They are owned by this exact scope, not by the physical host.
			List<(PlaybackVoice Voice, StreamingFilterChain? State)> updates = [];
			HashSet<PlaybackVoice> seen = [];
			void Consider(PlaybackVoice? voice)
			{
				if (voice is null || !seen.Add(voice)
					|| !VoiceBelongsToFlattenedSource(voice, controller.ScopeId))
					return;
				FilterChain? layers = BuildEnclosingEventFilters(
					voice.EnclosingSourceVolumes, controller, candidate);
				StreamingFilterChain? prepared =
					voice.PrepareEnclosingEventFilterUpdate(layers);
				updates.Add((voice, prepared));
			}
			foreach (PlaybackChannelState state in _channels.Values)
				Consider(state.CurrentVoice);
			foreach (PlaybackChannelState state in _scopedVirtualChannels.Values)
				Consider(state.CurrentVoice);
			foreach (PlaybackChannelState state in _targetedVirtualChannels.Values)
				Consider(state.CurrentVoice);
			foreach (PlaybackVoice voice in _virtualVoices)
				Consider(voice);
			controller.PublishEventFilterConfiguration(candidate);
			foreach (var update in updates)
				update.Voice.PublishEnclosingEventFilterUpdate(update.State);
		}
		catch (Exception exception) when (exception is ArgumentException
			or InvalidOperationException or KeyNotFoundException
			or OverflowException)
		{
			NoteFilterCommandRejected?.Invoke(exception.Message);
		}
	}

	private void BeginFlattenedSource(PlaybackChannelState channel,
		BeginFlattenedSourceVolumeCommand begin, long frame)
	{
		if (_sourceVolumes.ContainsKey(begin.ChildScopeId))
			throw new InvalidOperationException(
				"Flattened scope already has a volume controller.");
		channel.SynchronizeContinuousState(frame);
		if (channel.CurrentFlattenedSource is { } previous)
			channel.CaptureCurrentNoteVolume(previous.Read(frame));
		else if (channel.CurrentVoice is not null)
			channel.CaptureCurrentNoteVolume(
				channel.CurrentVoice.GetBaseNoteVolume(frame));
		DisplaceCurrentVoice(channel, frame);
		channel.ResetPanbrelloOffsetForNewNote();
		FlattenedSourceVolume source = new(begin.ChildScopeId,
			begin.InitialVolume, _tickClock,
			_context.Configuration.SampleRate,
			begin.EventFilterConfiguration);
		_sourceVolumes.Add(begin.ChildScopeId, source);
		channel.CurrentFlattenedSource = source;
		channel.CaptureCurrentNoteVolume(begin.InitialVolume);
	}

	private static FilterChain? BuildEnclosingEventFilters(
		IReadOnlyList<FlattenedSourceVolume> ancestors,
		FlattenedSourceVolume? replaced = null,
		FilterInstance? proposed = null)
	{
		// ParentSourceScopes are outer-to-inner. Each descendant's own
		// note Filter is processed before logical instigator filters;
		// therefore inherit innermost scope first. No DSP histories are
		// shared across different descendant voices.
		List<FilterInstance> layers = [];
		for (int i = ancestors.Count - 1; i >= 0; i--)
			{
				FilterInstance? filter = ReferenceEquals(ancestors[i], replaced)
					? proposed : ancestors[i].EventFilterConfiguration;
				if (filter is not null)
					layers.Add(filter);
			}
		return layers.Count == 0 ? null : new FilterChain(layers);
	}

	private double ReadInheritedPitchAt(long frame)
	{
		double value = InheritedPitchAtFrame?.Invoke(frame) ?? 1.0;
		if (!(value > 0.0) || !double.IsFinite(value))
			throw new InvalidOperationException(
				"Private parent pitch modulation must be positive and finite.");
		return value;
	}

	private void StartNote(
		int physicalChannel,
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame,
		long physicalPlaybackOwner)
	{
		channel.SynchronizeContinuousState(eventFrame);

		if (channel.CurrentFlattenedSource is { } previousSource)
			channel.CaptureCurrentNoteVolume(previousSource.Read(eventFrame));
		else if (channel.CurrentVoice is not null)
			channel.CaptureCurrentNoteVolume(
				channel.CurrentVoice.GetBaseNoteVolume(eventFrame));
		channel.CurrentFlattenedSource = null;

		DisplaceCurrentVoice(channel, eventFrame);
		channel.ResetPanbrelloOffsetForNewNote();

		if (!_soundResolver.TryResolve(start.SourceId, start.Mixdown, out ISound? sound)
			|| sound is null)
		{
			return;
		}

		double inheritedPitch = ReadInheritedPitchAt(eventFrame);
		SoundInvocation? invocation = sound.CreateInvocation(
			checked(start.PitchMultiplier * inheritedPitch),
			start.PlaybackSpeedMultiplier);
		if (invocation is null)
			return;

		// Direct volume is part of this note start. A source which cannot
		// actually start must not seed the channel volume for a later note.
		if (start.Volume.HasValue)
			channel.SetNoteVolume(start.Volume.Value);

		List<PlaybackChannelState> enclosingVolumes = [];
		if (start.ParentOverallChannels is { } parents)
			foreach (ParentVolumeChannel parent in parents)
				enclosingVolumes.Add(GetChannelState(parent.Host, parent.Owner));
		List<FlattenedSourceVolume> enclosingSources = [];
		if (start.ParentSourceScopes is { } sourceScopes)
			foreach (long sourceScope in sourceScopes)
			{
				if (!_sourceVolumes.TryGetValue(sourceScope,
					out FlattenedSourceVolume? sourceController))
					throw new InvalidOperationException(
						"Flattened source controller was not activated before its child note.");
				enclosingSources.Add(sourceController);
			}
		PlaybackVoice voice = new(
			invocation.Sound,
			invocation.State,
			invocation.Configuration,
			eventFrame,
			_context.Configuration.OutputChannelCount,
			_context.Configuration.SampleRate,
			channel.FilterParameters,
			channel.NoteVolume,
			channel.OverallVolume,
			_tickClock,
			_nextVoiceModulationSeed++,
			physicalChannel,
			sourceGainMultiplier: start.GainMultiplier,
			originPhysicalPlaybackOwner: physicalPlaybackOwner,
			enclosingVolumeChannels: enclosingVolumes,
			enclosingSourceVolumes: enclosingSources,
			inheritedPitch: InheritedPitchAtFrame,
			inheritedPitchAtStart: inheritedPitch,
			initialNotePitchMultiplier: start.PitchMultiplier,
			toneFilters: invocation.ToneFilters,
			eventFilter: start.EventFilterConfiguration,
			enclosingEventFilters: BuildEnclosingEventFilters(enclosingSources));

		voice.OriginPatternCursorId = _activePatternCursorId;
		channel.AttachVoice(voice, eventFrame);
	}

	private void ApplyPastNoteAction(
		int physicalChannel,
		PlaybackChannelState channel,
		TrackerPastNoteAction action,
		long eventFrame,
		long channelOwner)
	{
		for (int index = _virtualVoices.Count - 1; index >= 0; index--)
		{
			PlaybackVoice voice = _virtualVoices[index];
			if (voice.OriginVirtualChannelId.HasValue
				|| voice.OriginPhysicalChannel != physicalChannel
				|| voice.OriginPhysicalPlaybackOwner != channelOwner)
				continue;

			switch (action)
			{
				case TrackerPastNoteAction.Cut:
					voice.AddCutTo(channel.AntiClickTail);
					RetireDisplacedVoiceAt(index);
					break;

				case TrackerPastNoteAction.Off:
					voice.ApplyNoteOff(
						eventFrame,
						_context.Configuration.SampleRate);
					break;

				case TrackerPastNoteAction.Fade:
					voice.RequestNoteFade(
						eventFrame,
						_context.Configuration.SampleRate);
					break;

				default:
					throw new InvalidOperationException(
						$"Unsupported past-note action {action}.");
			}
		}
	}

	private void RetireDisplacedVoiceAt(int index)
	{
		PlaybackVoice voice = _virtualVoices[index];
		if (voice.DisplacedOutputFilterState is { HasTail: true } tail)
			_displacedOutputTails.Add(tail);
		_virtualVoices.RemoveAt(index);
	}

	private ChannelOutputFilterState? ForkHostFilter(PlaybackVoice voice)
	{
		if (voice.OriginVirtualChannelId is uint virtualId)
		{
			if (voice.OriginScopedVirtualOwner is long owner)
				return _scopedVirtualOutputFilters.TryGetValue(
					(owner, virtualId), out ChannelOutputFilterState? scoped)
					? scoped.Fork() : null;
			return _targetedVirtualOutputFilters.TryGetValue(
				virtualId, out ChannelOutputFilterState? targeted)
				? targeted.Fork() : null;
		}
		return _physicalOutputFilters.TryGetValue(
			voice.OriginPhysicalChannel, out ChannelOutputFilterState? physical)
			? physical.Fork() : null;
	}

	private void DisplaceCurrentVoice(
		PlaybackChannelState channel,
		long eventFrame)
	{
		PlaybackVoice? oldVoice = channel.DetachCurrentVoice();
		if (oldVoice is null)
			return;

		NewNoteAction action =
			oldVoice.NewNoteActionOverride
			?? oldVoice.Configuration.NewNotePolicy.Action;

		switch (action)
		{
			case NewNoteAction.Cut:
				oldVoice.AddCutTo(channel.AntiClickTail);
				break;

			case NewNoteAction.Continue:
				oldVoice.DisplacedOutputFilterState = ForkHostFilter(oldVoice);
				_virtualVoices.Add(oldVoice);
				break;

			case NewNoteAction.Off:
				oldVoice.ApplyNoteOff(
					eventFrame,
					_context.Configuration.SampleRate);
				// An already-complete volume envelope needs no
				// displaced-voice tail. Preserve legacy NNA bookkeeping
				// for source-level immediate Note Off (e.g. bare samples),
				// whose pool entries are retired during ordinary rendering.
				long? envelopeEnd =
					oldVoice.GetVolumeEnvelopeEndFrameExclusiveAfterNoteOff(
						eventFrame);
				if (!envelopeEnd.HasValue || eventFrame < envelopeEnd.Value)
				{
					oldVoice.DisplacedOutputFilterState = ForkHostFilter(oldVoice);
					_virtualVoices.Add(oldVoice);
				}
				break;

			case NewNoteAction.Fade:
				TimeSpan? fadeDuration =
					oldVoice.NewNoteActionOverride.HasValue
						? oldVoice.Configuration.NewNoteFadeDuration
						: oldVoice.Configuration.NewNotePolicy.FadeDuration;

				oldVoice.RequestNoteFade(
					eventFrame,
					_context.Configuration.SampleRate,
					fadeDuration);
				oldVoice.DisplacedOutputFilterState = ForkHostFilter(oldVoice);
				_virtualVoices.Add(oldVoice);
				break;

			default:
				throw new InvalidOperationException(
					$"Unsupported new-note action {action}.");
		}
	}

	private bool AnyChannelOutputFilterHasTail()
	{
		foreach (ChannelOutputFilterState state in _physicalOutputFilters.Values)
			if (state.HasTail) return true;
		foreach (ChannelOutputFilterState state in _targetedVirtualOutputFilters.Values)
			if (state.HasTail) return true;
		foreach (ChannelOutputFilterState state in _scopedVirtualOutputFilters.Values)
			if (state.HasTail) return true;
		return false;
	}

	/// <summary>Unconfigured hosts bypass the extra bus and preserve
	/// precisely the previous summation ordering. A configured host routes
	/// all participating voices to one buffer before filtering.</summary>
	private static void AccumulateHost<TKey>(
		TKey key, ReadOnlySpan<float> contribution, Span<float> destination,
		IReadOnlyDictionary<TKey, ChannelOutputFilterState> filters,
		Dictionary<TKey, float[]>? buses, int sampleCount) where TKey : notnull
	{
		if (!filters.ContainsKey(key))
		{
			AddBuffer(destination, contribution);
			return;
		}
		if (buses is null)
			throw new InvalidOperationException("Configured host bus was not prepared.");
		if (!buses.TryGetValue(key, out float[]? buffer))
		{
			buffer = ArrayPool<float>.Shared.Rent(sampleCount);
			buffer.AsSpan(0, sampleCount).Clear();
			buses.Add(key, buffer);
		}
		AddBuffer(buffer.AsSpan(0, sampleCount), contribution);
	}

	private static void FlushHostFilters<TKey>(
		IReadOnlyDictionary<TKey, ChannelOutputFilterState> filters,
		Dictionary<TKey, float[]>? buses,
		Span<float> destination, int sampleCount) where TKey : notnull
	{
		if (buses is null) return;
		foreach (var pair in filters)
		{
			bool hasBuffer = buses.TryGetValue(pair.Key, out float[]? buffer);
			if (!hasBuffer && !pair.Value.HasTail)
				continue;
			if (!hasBuffer)
			{
				buffer = ArrayPool<float>.Shared.Rent(sampleCount);
				buffer.AsSpan(0, sampleCount).Clear();
				buses.Add(pair.Key, buffer);
			}
			Span<float> samples = buffer!.AsSpan(0, sampleCount);
			pair.Value.ProcessInterleaved(samples);
			AddBuffer(destination, samples);
		}
	}

	private static void ReturnHostBuffers<TKey>(
		Dictionary<TKey, float[]>? buses) where TKey : notnull
	{
		if (buses is null) return;
		foreach (float[] buffer in buses.Values)
			ArrayPool<float>.Shared.Return(buffer);
	}

	private void RenderSegment(
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		int outputChannelCount = _context.Configuration.OutputChannelCount;
		int sampleCount = checked(frameCount * outputChannelCount);
		float[] rented = ArrayPool<float>.Shared.Rent(sampleCount);
		Dictionary<int, float[]>? physicalBuses =
			_physicalOutputFilters.Count == 0 ? null : [];
		Dictionary<uint, float[]>? virtualBuses =
			_targetedVirtualOutputFilters.Count == 0 ? null : [];
		Dictionary<(long Owner, uint Id), float[]>? scopedBuses =
			_scopedVirtualOutputFilters.Count == 0 ? null : [];

		try
		{
			foreach (KeyValuePair<(long Owner, int Host), PlaybackChannelState> pair in _channels)
			{
				PlaybackChannelState channel = pair.Value;
				Span<float> channelBuffer = rented.AsSpan(0, sampleCount);
				channelBuffer.Clear();

				if (channel.HasActiveContinuousState
					|| channel.HasActiveTremor)
				{
					for (int frame = 0; frame < frameCount; frame++)
					{
						long absoluteFrame =
							absoluteStartFrame + frame;
						channel.SynchronizeContinuousState(
							absoluteFrame);

						PlaybackVoice? voice = channel.CurrentVoice;
						channel.SynchronizeTremor(
							absoluteFrame,
							voice is not null);

						if (voice is null)
							continue;

						Span<float> outputFrame =
							channelBuffer.Slice(
								frame * outputChannelCount,
								outputChannelCount);

						bool finished = RenderVoice(
							voice,
							absoluteFrame,
							1,
							outputFrame);

						double tremorGain = channel.TremorGain;
						if (tremorGain != 1.0)
						{
							for (int outputChannel = 0;
								outputChannel < outputChannelCount;
								outputChannel++)
							{
								outputFrame[outputChannel] =
									(float)(
										outputFrame[outputChannel]
										* tremorGain);
							}
						}

						if (finished)
							channel.DetachCurrentVoice();
					}
				}
				else if (channel.CurrentVoice is not null)
				{
					channel.SynchronizeContinuousState(
						absoluteStartFrame);

					bool finished = RenderVoice(
						channel.CurrentVoice,
						absoluteStartFrame,
						frameCount,
						channelBuffer);

					if (finished)
						channel.DetachCurrentVoice();
				}

				for (int frame = 0; frame < frameCount; frame++)
				{
					Span<float> outputFrame = channelBuffer.Slice(
						frame * outputChannelCount,
						outputChannelCount);
					channel.AntiClickTail.RenderFrame(outputFrame);
				}

				AccumulateHost(pair.Key.Host, channelBuffer, destination,
					_physicalOutputFilters, physicalBuses, sampleCount);
			}

			List<uint>? emptyVirtualChannels = null;
			foreach (KeyValuePair<uint, PlaybackChannelState> pair
				in _targetedVirtualChannels)
			{
				PlaybackChannelState channel = pair.Value;
				Span<float> channelBuffer =
					rented.AsSpan(0, sampleCount);
				channelBuffer.Clear();

				if (channel.CurrentVoice is not null)
				{
					bool finished = RenderVoice(
						channel.CurrentVoice,
						absoluteStartFrame,
						frameCount,
						channelBuffer);
					if (finished)
						channel.DetachCurrentVoice();
				}

				for (int frame = 0; frame < frameCount; frame++)
				{
					Span<float> outputFrame =
						channelBuffer.Slice(
							frame * outputChannelCount,
							outputChannelCount);
					channel.AntiClickTail.RenderFrame(outputFrame);
				}

				AccumulateHost(pair.Key, channelBuffer, destination,
					_targetedVirtualOutputFilters, virtualBuses, sampleCount);

				if (channel.CurrentVoice is null
					&& !channel.AntiClickTail.IsActive)
				{
					emptyVirtualChannels ??= [];
					emptyVirtualChannels.Add(pair.Key);
				}
			}

			if (emptyVirtualChannels is not null)
			{
				foreach (uint channelId in emptyVirtualChannels)
					_targetedVirtualChannels.Remove(channelId);
			}

			// Invocation-scoped virtual channels are mixed exactly as
			// ordinary targeted virtual voices, preserving every output
			// speaker feed. Scope is an identity key, not an audio bus.
			List<(long Owner, uint Id)>? emptyScoped = null;
			foreach (var pair in _scopedVirtualChannels)
			{
				PlaybackChannelState channel = pair.Value;
				Span<float> channelBuffer = rented.AsSpan(0, sampleCount);
				channelBuffer.Clear();
				if (channel.CurrentVoice is not null)
				{
					bool finished = RenderVoice(
						channel.CurrentVoice, absoluteStartFrame,
						frameCount, channelBuffer);
					if (finished)
						channel.DetachCurrentVoice();
				}
				for (int frame = 0; frame < frameCount; frame++)
					channel.AntiClickTail.RenderFrame(channelBuffer.Slice(
						frame * outputChannelCount, outputChannelCount));
				AccumulateHost(pair.Key, channelBuffer, destination,
					_scopedVirtualOutputFilters, scopedBuses, sampleCount);
				if (channel.CurrentVoice is null
					&& !channel.AntiClickTail.IsActive)
				{
					emptyScoped ??= [];
					emptyScoped.Add(pair.Key);
				}
			}
			if (emptyScoped is not null)
				foreach (var key in emptyScoped)
					_scopedVirtualChannels.Remove(key);

			// A dynamic host may outlive its displaced source voice while
			// its finite filter response decays. Render old tails before
			// the active displaced voices; freshly retired voices join next
			// segment, so none is processed twice for one PCM frame.
			for (int index = 0; index < _displacedOutputTails.Count;)
			{
				ChannelOutputFilterState tail = _displacedOutputTails[index];
				Span<float> tailBuffer = rented.AsSpan(0, sampleCount);
				tailBuffer.Clear();
				tail.ProcessInterleaved(tailBuffer);
				AddBuffer(destination, tailBuffer);
				if (!tail.HasTail)
					_displacedOutputTails.RemoveAt(index);
				else
					index++;
			}

			for (int index = 0; index < _virtualVoices.Count;)
			{
				PlaybackVoice voice = _virtualVoices[index];
				Span<float> voiceBuffer = rented.AsSpan(0, sampleCount);
				voiceBuffer.Clear();

				bool finished = RenderVoice(
					voice,
					absoluteStartFrame,
					frameCount,
					voiceBuffer);

				// The NNA voice is now its own dynamic virtual host.
				// Forked history is processed once on its own output feed:
				// never run this voice through the originating physical or
				// scoped virtual host's *continuing* Filter a second time.
				if (voice.DisplacedOutputFilterState is { } displaced)
					displaced.ProcessInterleaved(voiceBuffer);
				AddBuffer(destination, voiceBuffer);

				if (finished)
					RetireDisplacedVoiceAt(index);
				else
					index++;
			}

			// A host's current voice, owner-scoped child voices and anti-click
			// residue have all been summed at this boundary. Displaced
			// NNA voices have independent forked dynamic virtual hosts.
			// The configured channel Filter runs once on that complete feed,
			// before global volume and the final speaker filter stage.
			FlushHostFilters(_physicalOutputFilters, physicalBuses,
				destination, sampleCount);
			FlushHostFilters(_targetedVirtualOutputFilters, virtualBuses,
				destination, sampleCount);
			FlushHostFilters(_scopedVirtualOutputFilters, scopedBuses,
				destination, sampleCount);

			ApplyGlobalVolume(
				absoluteStartFrame,
				frameCount,
				outputChannelCount,
				destination);
			ReclaimRetiredPhysicalScopes();
		}
		finally
		{
			ReturnHostBuffers(physicalBuses);
			ReturnHostBuffers(virtualBuses);
			ReturnHostBuffers(scopedBuses);
			ArrayPool<float>.Shared.Return(rented);
		}
	}

	private void SetBaselineTempo(
		long absoluteFrame,
		double tempo)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0)
			|| double.IsNaN(tempo)
			|| double.IsInfinity(tempo))
		{
			throw new ArgumentOutOfRangeException(nameof(tempo));
		}

		CommitTempoRamp(absoluteFrame);
		_tempo = tempo;
	}

	private void SetTempoRamp(
		long absoluteFrame,
		double endingTempo,
		double trackerTicks)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(endingTempo > 0.0)
			|| double.IsNaN(endingTempo)
			|| double.IsInfinity(endingTempo))
		{
			throw new ArgumentOutOfRangeException(nameof(endingTempo));
		}
		if (!(trackerTicks > 0.0)
			|| double.IsNaN(trackerTicks)
			|| double.IsInfinity(trackerTicks))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerTicks));
		}

		CommitTempoRamp(absoluteFrame);

		LinearRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.Tempo,
			totalDelta: endingTempo - _tempo,
			rowSpan: trackerTicks,
			commitOnExpire: true);
		_globalOperators.Add(playbackOperator);
		_activeTempoRamp = new ActiveTempoRamp
		{
			StartFrame = absoluteFrame,
			TrackerTicks = trackerTicks,
			Operator = playbackOperator,
		};
	}

	private void UpdateTempoRamp(long absoluteFrame)
	{
		ActiveTempoRamp? ramp = _activeTempoRamp;
		if (ramp is null)
			return;

		double rowTime = Math.Clamp(
			_tickClock.GetElapsedTicks(
				ramp.StartFrame,
				Math.Max(ramp.StartFrame, absoluteFrame)),
			0.0,
			ramp.TrackerTicks);

		ramp.Operator.Update(
			absoluteFrame
				/ (double)_context.Configuration.SampleRate,
			rowTime);
	}

	private void SynchronizeTempoRamp(long absoluteFrame)
	{
		ActiveTempoRamp? ramp = _activeTempoRamp;
		if (ramp is null)
			return;

		UpdateTempoRamp(absoluteFrame);
		double rowTime = Math.Clamp(
			_tickClock.GetElapsedTicks(
				ramp.StartFrame,
				Math.Max(ramp.StartFrame, absoluteFrame)),
			0.0,
			ramp.TrackerTicks);

		if (rowTime < ramp.TrackerTicks - 1e-9)
			return;

		CommitTempoRamp(absoluteFrame);
	}

	private void CommitTempoRamp(long absoluteFrame)
	{
		ActiveTempoRamp? ramp = _activeTempoRamp;
		if (ramp is null)
			return;

		double rowTime = Math.Clamp(
			_tickClock.GetElapsedTicks(
				ramp.StartFrame,
				Math.Max(ramp.StartFrame, absoluteFrame)),
			0.0,
			ramp.TrackerTicks);

		PlaybackParameterDeltas committed =
			_globalOperators.Expire(
				ramp.Operator,
				absoluteFrame
					/ (double)_context.Configuration.SampleRate,
				rowTime);

		_tempo += committed[PlaybackParameter.Tempo];
		_activeTempoRamp = null;
	}
	private void SetGlobalVolume(
		long absoluteFrame,
		double volume)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(volume)
			|| double.IsInfinity(volume)
			|| volume < 0.0
			|| volume > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}

		_globalVolume = volume;
	}

	private void AdjustGlobalVolume(
		long absoluteFrame,
		double trackerUnits)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (double.IsNaN(trackerUnits)
			|| double.IsInfinity(trackerUnits))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerUnits));
		}

		_globalVolume = Math.Clamp(
			_globalVolume + trackerUnits / 128.0,
			0.0,
			1.0);
	}

	private void SetGlobalVolumeSlide(
		int physicalChannel,
		long absoluteFrame,
		int ticksPerRow,
		double trackerUnitsPerTick,
		long physicalPlaybackOwner)
	{
		if (physicalChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannel));
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (double.IsNaN(trackerUnitsPerTick)
			|| double.IsInfinity(trackerUnitsPerTick))
		{
			throw new ArgumentOutOfRangeException(
				nameof(trackerUnitsPerTick));
		}

		if (_globalVolumeSlides.ContainsKey((physicalPlaybackOwner, physicalChannel)))
		{
			CommitGlobalVolumeSlide(
				physicalChannel,
				absoluteFrame, physicalPlaybackOwner);
		}

		LinearRowPlaybackOperator playbackOperator = new(
			PlaybackParameter.GlobalVolume,
			totalDelta:
				trackerUnitsPerTick
					* Math.Max(0, ticksPerRow - 1)
					/ 128.0,
			rowSpan: ticksPerRow,
			commitOnExpire: true);
		_globalOperators.Add(playbackOperator);

		_globalVolumeSlides[(physicalPlaybackOwner, physicalChannel)] =
			new ActiveGlobalVolumeSlide
			{
				StartFrame = absoluteFrame,
				TicksPerRow = ticksPerRow,
				Operator = playbackOperator,
			};
	}

	private void ClearGlobalVolumeSlide(
		int physicalChannel,
		long absoluteFrame,
		long physicalPlaybackOwner)
	{
		if (physicalChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannel));
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		CommitGlobalVolumeSlide(
			physicalChannel,
			absoluteFrame, physicalPlaybackOwner);
	}

	private void CommitGlobalVolumeSlide(
		int physicalChannel,
		long absoluteFrame,
		long physicalPlaybackOwner)
	{
		if (!_globalVolumeSlides.Remove(
			(physicalPlaybackOwner, physicalChannel),
			out ActiveGlobalVolumeSlide? slide))
		{
			return;
		}

		double rowTime = Math.Clamp(
			_tickClock.GetElapsedTicks(
				slide.StartFrame,
				Math.Max(slide.StartFrame, absoluteFrame)),
			0.0,
			slide.TicksPerRow);

		PlaybackParameterDeltas committed =
			_globalOperators.Expire(
				slide.Operator,
				absoluteFrame
					/ (double)_context.Configuration.SampleRate,
				rowTime);

		_globalVolume = Math.Clamp(
			_globalVolume
				+ committed[PlaybackParameter.GlobalVolume],
			0.0,
			1.0);
	}

	private double GetEffectiveGlobalVolume(long absoluteFrame)
	{
		for (int index = _nativeGlobal.Count - 1; index >= 0; index--)
		{
			ActiveParametricEffect effect = _nativeGlobal[index];
			if (!effect.Advance(absoluteFrame, _globalOperators,
				out PlaybackParameterDeltas? delta))
				continue;
			_globalVolume = Math.Clamp(_globalVolume +
				delta![PlaybackParameter.GlobalVolume], 0.0, 1.0);
			_nativeGlobal.RemoveAt(index);
		}

		foreach (
			ActiveGlobalVolumeSlide slide
				in _globalVolumeSlides.Values)
		{
			double rowTime = Math.Clamp(
				_tickClock.GetElapsedTicks(
					slide.StartFrame,
					Math.Max(
						slide.StartFrame,
						absoluteFrame)),
				0.0,
				slide.TicksPerRow);

			slide.Operator.Update(
				absoluteFrame
					/ (double)_context.Configuration.SampleRate,
				rowTime);
		}

		return Math.Clamp(
			_globalVolume
				+ _globalOperators.GetTotalDelta(
					PlaybackParameter.GlobalVolume),
			0.0,
			1.0);
	}


	private void ApplyGlobalVolume(
		long absoluteStartFrame,
		int frameCount,
		int outputChannelCount,
		Span<float> destination)
	{
		for (int frame = 0; frame < frameCount; frame++)
		{
			long absoluteFrame = absoluteStartFrame + frame;
			UpdateTempoRamp(absoluteFrame);
			double effectiveVolume =
				GetEffectiveGlobalVolume(absoluteFrame);

			if (effectiveVolume == 1.0)
				continue;

			Span<float> outputFrame = destination.Slice(
				frame * outputChannelCount,
				outputChannelCount);

			for (int outputChannel = 0;
				outputChannel < outputChannelCount;
				outputChannel++)
			{
				outputFrame[outputChannel] =
					(float)(
						outputFrame[outputChannel]
						* effectiveVolume);
			}
		}
	}

	private bool RenderVoice(
		PlaybackVoice voice,
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		if (frameCount > 1
			&& (voice.HasPanningEnvelope || voice.HasActiveNativeParametric))
		{
			int panningOutputChannelCount =
				_context.Configuration.OutputChannelCount;
			for (int frame = 0; frame < frameCount; frame++)
			{
				Span<float> outputFrame = destination.Slice(
					frame * panningOutputChannelCount,
					panningOutputChannelCount);
				if (RenderVoice(
					voice,
					absoluteStartFrame + frame,
					1,
					outputFrame))
				{
					return true;
				}
			}
			return false;
		}

		voice.SynchronizeNativeParametric(absoluteStartFrame);
		voice.SynchronizePanningEnvelope(absoluteStartFrame);

		long invocationStartFrame = absoluteStartFrame - voice.StartFrame;
		if (invocationStartFrame < 0)
			throw new InvalidOperationException("A playback voice began after the segment being rendered.");
		long? effectiveEndAbsolute =
			GetEffectiveVoiceEndFrameExclusive(
				voice,
				absoluteStartFrame);

		if (effectiveEndAbsolute.HasValue
			&& absoluteStartFrame >= effectiveEndAbsolute.Value)
		{
			return true;
		}

		int activeFrames = frameCount;
		if (effectiveEndAbsolute.HasValue)
		{
			long remaining = effectiveEndAbsolute.Value - absoluteStartFrame;
			activeFrames = (int)Math.Min(activeFrames, remaining);
		}

		int outputChannelCount = _context.Configuration.OutputChannelCount;
		Span<float> activeDestination = destination.Slice(
			0,
			checked(activeFrames * outputChannelCount));

		int sourceFrames = activeFrames;
		if (voice.ConfiguredFilterTailFrames > 0
			|| voice.ToneFilterState is not null
			|| voice.EventFilterState is not null
			|| voice.EnclosingEventFilterState is not null)
		{
			long? sourceEnd = voice.Sound.GetEndFrameExclusive(
				_context, voice.SoundState);
			if (voice.SoundState.NaturalEndFrameExclusive is long natural)
				sourceEnd = sourceEnd.HasValue
					? Math.Min(sourceEnd.Value, natural) : natural;
			if (sourceEnd.HasValue)
			{
				sourceFrames = (int)Math.Min(activeFrames,
					Math.Max(0, sourceEnd.Value - invocationStartFrame));
				activeDestination.Slice(sourceFrames * outputChannelCount).Clear();
			}
		}
		if (sourceFrames > 0)
			voice.Sound.Render(
				_context, voice.SoundState, invocationStartFrame,
				sourceFrames, activeDestination.Slice(0,
					checked(sourceFrames * outputChannelCount)));

		// A streaming sound (notably a coroutine-backed private
		// Pattern/Sequence mixdown) can discover its exact end *during*
		// this Render call. Re-evaluate before deciding which voices to
		// detach: otherwise a single long tail block leaves completed
		// NNA voices and transient private sound registrations live until
		// an unrelated future Render call, even after their PCM ends.
		if (voice.Sound is IStreamingFiniteSound)
		{
			long? observedEnd = GetEffectiveVoiceEndFrameExclusive(
				voice, absoluteStartFrame + activeFrames);
			if (observedEnd.HasValue)
			{
				effectiveEndAbsolute = effectiveEndAbsolute.HasValue
					? Math.Min(effectiveEndAbsolute.Value, observedEnd.Value)
					: observedEnd.Value;
				long remaining = effectiveEndAbsolute.Value - absoluteStartFrame;
				activeFrames = remaining <= 0
					? 0
					: (int)Math.Min(activeFrames, remaining);
				activeDestination = destination.Slice(0,
					checked(activeFrames * outputChannelCount));
			}
		}

		if (voice.SoundState.NaturalEndFrameExclusive is long discovered)
		{
			long discoveredRemaining = discovered - invocationStartFrame;
			if (voice.ConfiguredFilterTailFrames > 0
				|| voice.ToneFilterState is not null
				|| voice.EventFilterState is not null
				|| voice.EnclosingEventFilterState is not null)
			{
				int sourceEnd = discoveredRemaining <= 0 ? 0
					: (int)Math.Min(activeFrames, discoveredRemaining);
				activeDestination.Slice(sourceEnd * outputChannelCount).Clear();
				long tailEnd = AddSaturating(
					discovered, voice.ConfiguredFilterTailFrames);
				long remaining = tailEnd - invocationStartFrame;
				activeFrames = remaining <= 0 ? 0
					: (int)Math.Min(activeFrames, remaining);
				activeDestination = destination.Slice(0,
					checked(activeFrames * outputChannelCount));
			}
			else
			{
				activeFrames = discoveredRemaining <= 0 ? 0
					: (int)Math.Min(activeFrames, discoveredRemaining);
				activeDestination = destination.Slice(0,
					checked(activeFrames * outputChannelCount));
			}
		}

		for (int frame = 0; frame < activeFrames; frame++)
		{
			long absoluteFrame = absoluteStartFrame + frame;
			double volume = voice.SourceGainMultiplier;
			foreach (FlattenedSourceVolume source in voice.EnclosingSourceVolumes)
				volume *= source.Read(absoluteFrame);
			foreach (PlaybackChannelState enclosing in voice.EnclosingVolumeChannels)
				volume *= enclosing.ReadEffectiveOverallVolume(absoluteFrame);
			volume *= voice.GetNoteVolume(absoluteFrame)
				* voice.GetVolumeEnvelopeValue(absoluteFrame)
				* voice.OverallVolume
				* voice.GetFadeGain(
					absoluteFrame,
					_context.Configuration.SampleRate);

			Span<float> outputFrame = activeDestination.Slice(
				frame * outputChannelCount,
				outputChannelCount);

			voice.ToneFilterState?.ProcessFrame(outputFrame);
			voice.SynchronizeEventFilterParameterEnvelopes(
				absoluteFrame, NoteFilterCommandRejected);
			voice.EventFilterState?.ProcessFrame(outputFrame);
			voice.EnclosingEventFilterState?.ProcessFrame(outputFrame);
			if (voice.HasFilterEnvelope)
				voice.SynchronizeFilterEnvelope(absoluteFrame);
			voice.FilterState.ProcessFrame(outputFrame);

			for (int outputChannel = 0; outputChannel < outputChannelCount; outputChannel++)
				outputFrame[outputChannel] = (float)(outputFrame[outputChannel] * volume);

			// Native IT surround is stereo phase encoding.
			if (voice.Surround && outputChannelCount == 2)
				outputFrame[1] = -outputFrame[1];

			voice.ObserveOutputFrame(outputFrame);
		}

		if (voice.SoundState.NaturalEndFrameExclusive is long naturalEnd
			&& invocationStartFrame + activeFrames
				>= AddSaturating(naturalEnd,
					voice.ConfiguredFilterTailFrames))
			return true;

		return effectiveEndAbsolute.HasValue
			&& absoluteStartFrame + activeFrames >= effectiveEndAbsolute.Value;
	}

	private long? GetEffectiveVoiceEndFrameExclusive(
		PlaybackVoice voice,
		long absoluteFrame)
	{
		long? soundEndRelative =
			voice.Sound.GetEndFrameExclusive(
				_context,
				voice.SoundState);
		if (voice.SoundState.NaturalEndFrameExclusive is long naturalEnd)
			soundEndRelative = soundEndRelative.HasValue
				? Math.Min(soundEndRelative.Value, naturalEnd) : naturalEnd;
		if (soundEndRelative.HasValue && voice.ConfiguredFilterTailFrames > 0)
			soundEndRelative = AddSaturating(soundEndRelative.Value,
				voice.ConfiguredFilterTailFrames);
		long? effectiveEndAbsolute =
			soundEndRelative.HasValue
				? AddSaturating(
					voice.StartFrame,
					soundEndRelative.Value)
				: null;

		if (voice.FadeEndFrameExclusive.HasValue)
		{
			effectiveEndAbsolute =
				effectiveEndAbsolute.HasValue
					? Math.Min(
						effectiveEndAbsolute.Value,
						voice.FadeEndFrameExclusive.Value)
					: voice.FadeEndFrameExclusive.Value;
		}

		long? envelopeEnd =
			voice.GetVolumeEnvelopeEndFrameExclusiveAfterNoteOff(
				absoluteFrame);
		if (envelopeEnd.HasValue)
		{
			effectiveEndAbsolute =
				effectiveEndAbsolute.HasValue
					? Math.Min(
						effectiveEndAbsolute.Value,
						envelopeEnd.Value)
					: envelopeEnd.Value;
		}

		return effectiveEndAbsolute;
	}

	private void CullFinishedCurrentVoice(
		PlaybackChannelState channel,
		long absoluteFrame)
	{
		PlaybackVoice? voice = channel.CurrentVoice;
		if (voice is null)
			return;

		// A volume envelope can finish at this exact Note Off frame
		// even when its terminal sound (for example an Instrument-bound
		// looping SampleSound) has no source end. Use the same composed
		// source/fade/envelope deadline as the PCM renderer, not just the
		// underlying sound's natural end.
		long? effectiveEnd = GetEffectiveVoiceEndFrameExclusive(
			voice, absoluteFrame);

		if (effectiveEnd.HasValue && absoluteFrame >= effectiveEnd.Value)
			channel.DetachCurrentVoice();
	}

	private static void AddBuffer(Span<float> destination, ReadOnlySpan<float> source)
	{
		for (int sample = 0; sample < destination.Length; sample++)
			destination[sample] += source[sample];
	}

	private static long AddSaturating(long left, long right)
	{
		if (right > 0 && left > long.MaxValue - right)
			return long.MaxValue;
		if (right < 0 && left < long.MinValue - right)
			return long.MinValue;
		return left + right;
	}
}
