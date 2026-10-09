using System;
using System.Buffers;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
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

	private readonly RenderContext _context;
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
	private readonly SortedDictionary<(long Owner, int Host), ActiveGlobalVolumeSlide>
		_globalVolumeSlides = [];
	private readonly PlaybackOperatorCollection _globalOperators = new();

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
		double initialTempo = SequencingConstants.DefaultTempo)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
		_soundResolver = soundResolver ?? throw new ArgumentNullException(nameof(soundResolver));
		if (!(initialTempo > 0.0) || !double.IsFinite(initialTempo))
			throw new ArgumentOutOfRangeException(nameof(initialTempo));
		_tempo = initialTempo;
		_tickClock = new TrackerTickClock(
			_schedule, _context.Configuration.SampleRate, initialTempo);
	}

	public long NextFrame => _nextFrame;

	public int SampleRate => _context.Configuration.SampleRate;

	public int OutputChannelCount => _context.Configuration.OutputChannelCount;

	public double GlobalVolume => _globalVolume;

	public int ActiveGlobalOperatorCount => _globalOperators.Count;


	public bool InputEnded => _inputEnded;

	public bool IsQuiescent
	{
		get
		{
			if (!_inputEnded
				|| _nextEventIndex < _schedule.Count
				|| _virtualVoices.Count != 0)
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
	public void RequestFadeOfActiveVoices()
	{
		foreach (PlaybackChannelState channel in _channels.Values)
			channel.CurrentVoice?.RequestNoteFade(
				_nextFrame, SampleRate);
		foreach (PlaybackChannelState channel in _targetedVirtualChannels.Values)
			channel.CurrentVoice?.RequestNoteFade(
				_nextFrame, SampleRate);
		foreach (PlaybackChannelState channel in _scopedVirtualChannels.Values)
			channel.CurrentVoice?.RequestNoteFade(
				_nextFrame, SampleRate);
		foreach (PlaybackVoice voice in _virtualVoices)
			voice.RequestNoteFade(_nextFrame, SampleRate);
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
			_virtualVoices.RemoveAt(index);
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
					commands, _nextFrame, cutIndefiniteAfterNoteOff: false);
				return;
			case ChannelTargetKind.AllVirtualInScope:
			case ChannelTargetKind.AllVirtual:
				ApplyVirtualBroadcast(invocationId,
					target.Kind == ChannelTargetKind.AllVirtual,
					commands, _nextFrame);
				return;
			case ChannelTargetKind.Physical:
				ApplyEvent(new NoteEvent(
					new MusicalTime(FrameTime.FrameStartTime(
						_nextFrame, _context.Configuration.SampleRate), 0),
					target, commands)
				{
					PhysicalPlaybackOwner = physicalPlaybackOwner,
				}, _nextFrame);
				return;
			default:
				ApplyLiveEvent(target, commands);
				return;
		}
	}

	/// <summary>
	/// Cancel the audible virtual voices owned by an invocation without
	/// disturbing identically numbered IDs from sibling invocations.
	/// Normal Pattern completion does not call this: notes may tail out.
	/// </summary>
	public void CancelScopedVoices(long invocationId)
	{
		if (invocationId < 0)
			throw new ArgumentOutOfRangeException(nameof(invocationId));
		foreach (var pair in _scopedVirtualChannels)
			if (pair.Key.Owner == invocationId)
				pair.Value.CutCurrentVoice();
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
				_virtualVoices.RemoveAt(i);
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
			commands, eventFrame, cutIndefiniteAfterNoteOff);

	private void ApplyVirtualCommands(
		PlaybackChannelState channel,
		IReadOnlyList<NoteCommand> commands,
		long eventFrame,
		bool cutIndefiniteAfterNoteOff)
	{
		foreach (NoteCommand command in commands)
		{
			switch (command)
			{
				case StartNoteCommand start:
					StartVirtualNote(
						channel,
						start,
						eventFrame);
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

				default:
					throw new NotSupportedException(
						$"Render command {command.GetType().Name} is not supported on an explicitly targeted virtual channel.");
			}
		}
	}

	private void StartVirtualNote(
		PlaybackChannelState channel,
		StartNoteCommand start,
		long eventFrame)
	{
		channel.CutCurrentVoice();

		if (!_soundResolver.TryResolve(
			start.SourceId,
			start.Mixdown,
			out ISound? sound)
			|| sound is null)
		{
			return;
		}

		SoundInvocation? invocation =
			sound.CreateInvocation(
				start.PitchMultiplier,
				start.PlaybackSpeedMultiplier);
		if (invocation is null)
			return;

		if (start.Volume.HasValue)
			channel.SetNoteVolume(start.Volume.Value);

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
			sourceGainMultiplier: start.GainMultiplier);

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

			case StartNoteCommand start:
				StartNote(
					physicalChannel,
					channel,
					start,
					eventFrame,
					physicalPlaybackOwner);
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

			case SetPlaybackOffsetCommand playbackOffset:
				if (channel.CurrentVoice is not null)
					channel.CurrentVoice.SoundState.PlaybackOffset = playbackOffset.Offset;
				break;

			case SetSourceFrameOffsetCommand sourceFrameOffset:
				if (channel.CurrentVoice is not null
					&& channel.CurrentVoice.Sound is ISourceFrameSeekableSound seekable)
				{
					seekable.SetSourceFrameOffset(
						channel.CurrentVoice.SoundState,
						sourceFrameOffset.SourceFrameOffset);
				}
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
			_context.Configuration.SampleRate);
		_sourceVolumes.Add(begin.ChildScopeId, source);
		channel.CurrentFlattenedSource = source;
		channel.CaptureCurrentNoteVolume(begin.InitialVolume);
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

		SoundInvocation? invocation = sound.CreateInvocation(
			start.PitchMultiplier,
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
			enclosingSourceVolumes: enclosingSources);

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
			if (voice.OriginPhysicalChannel != physicalChannel
				|| voice.OriginPhysicalPlaybackOwner != channelOwner)
				continue;

			switch (action)
			{
				case TrackerPastNoteAction.Cut:
					voice.AddCutTo(channel.AntiClickTail);
					_virtualVoices.RemoveAt(index);
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
				_virtualVoices.Add(oldVoice);
				break;

			case NewNoteAction.Off:
				oldVoice.ApplyNoteOff(
					eventFrame,
					_context.Configuration.SampleRate);
				_virtualVoices.Add(oldVoice);
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
				_virtualVoices.Add(oldVoice);
				break;

			default:
				throw new InvalidOperationException(
					$"Unsupported new-note action {action}.");
		}
	}

	private void RenderSegment(
		long absoluteStartFrame,
		int frameCount,
		Span<float> destination)
	{
		int outputChannelCount = _context.Configuration.OutputChannelCount;
		int sampleCount = checked(frameCount * outputChannelCount);
		float[] rented = ArrayPool<float>.Shared.Rent(sampleCount);

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

				AddBuffer(destination, channelBuffer);
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

				AddBuffer(
					destination,
					channelBuffer);

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
				AddBuffer(destination, channelBuffer);
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

				AddBuffer(destination, voiceBuffer);

				if (finished)
					_virtualVoices.RemoveAt(index);
				else
					index++;
			}

			ApplyGlobalVolume(
				absoluteStartFrame,
				frameCount,
				outputChannelCount,
				destination);
			ReclaimRetiredPhysicalScopes();
		}
		finally
		{
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
		if (frameCount > 1 && voice.HasPanningEnvelope)
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

		voice.Sound.Render(
			_context,
			voice.SoundState,
			invocationStartFrame,
			activeFrames,
			activeDestination);

		if (voice.SoundState.NaturalEndFrameExclusive.HasValue)
		{
			long discoveredRemaining =
				voice.SoundState.NaturalEndFrameExclusive.Value
				- invocationStartFrame;

			activeFrames = discoveredRemaining <= 0
				? 0
				: (int)Math.Min(activeFrames, discoveredRemaining);

			activeDestination = destination.Slice(
				0,
				checked(activeFrames * outputChannelCount));
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

		if (voice.SoundState.NaturalEndFrameExclusive.HasValue
			&& invocationStartFrame + activeFrames
				>= voice.SoundState.NaturalEndFrameExclusive.Value)
		{
			return true;
		}

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

		long relativeFrame = Math.Max(0, absoluteFrame - voice.StartFrame);
		long? endFrame = voice.Sound.GetEndFrameExclusive(
			_context,
			voice.SoundState);

		if (endFrame.HasValue && relativeFrame >= endFrame.Value)
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
