using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Instruments;
using Heresy.Render.Instruments;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

/// <summary>
/// Owns the snapshot-isolated recursive timeline, renderer and nested voices.
/// The same plan now powers production realtime transport and offline export.
/// </summary>
public sealed class PreparedIncrementalPlaybackPlan : IDisposable
{
	private bool _disposed;
	private BufferedAudioOutputSource? _renderingWorker;
	private readonly Action _disposePrivateMixdowns;

	internal PreparedIncrementalPlaybackPlan(
		SongDocumentSnapshot snapshot, IncrementalRecursiveTimeline timeline,
		PlaybackSession session, PreparedIncrementalAudioSource source,
		long rootInvocationId, Action disposePrivateMixdowns,
		SequencingContext sequencingContext)
	{
		Snapshot = snapshot;
		Timeline = timeline;
		Session = session;
		Source = source;
		RootInvocationId = rootInvocationId;
		_disposePrivateMixdowns = disposePrivateMixdowns;
		SequencingContext = sequencingContext;
	}

	public SongDocumentSnapshot Snapshot { get; }
	public IncrementalRecursiveTimeline Timeline { get; }
	public PlaybackSession Session { get; }
	public PreparedIncrementalAudioSource Source { get; }
	public long RootInvocationId { get; }
	public SequencingContext SequencingContext { get; }

	/// <summary>
	/// Start the dedicated worker that both generates notes and renders
	/// recursive PCM, publishing into a bounded callback-consumed ring.
	/// Stop audio callbacks before disposing the plan.
	/// </summary>
	public BufferedAudioOutputSource StartRendering(
		int capacityFrames, int blockFrames = 256)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_renderingWorker is not null)
			throw new InvalidOperationException(
				"A recursive playback plan already has a PCM worker.");
		return _renderingWorker = new BufferedAudioOutputSource(
			Source, capacityFrames, Math.Min(blockFrames, capacityFrames));
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_renderingWorker?.Dispose();
		Source.Dispose();
		Timeline.Dispose();
		_disposePrivateMixdowns();
	}
}

/// <summary>
/// Production recursive Pattern/Sequence execution as sample-accurate PCM,
/// using immutable song definitions, prepared scripts and decoded samples.
/// The coroutine advances on the dedicated rendering worker or export thread.
/// </summary>
public sealed class PreparedIncrementalPlaybackFactory
{
	/// <summary>
	/// An ad-hoc note schedule is already an ordered set of resolved commands;
	/// feed it into the ordinary incremental resolver as a temporary raw
	/// Pattern, rather than expanding nested sources greedily.
	/// </summary>
	private sealed class AdHocPattern : PatternDefinition,
		IIncrementalRawPatternNoteGenerator
	{
		private readonly NoteSchedule _schedule;
		public AdHocPattern(ObjectId id, NoteSchedule schedule)
			: base(id, "Live audition")
		{
			_schedule = schedule;
			RowCount = Math.Max(1, schedule.Count == 0 ? 1
				: (int)Math.Ceiling(schedule.Max(note => note.Offset.RowOffset)) + 1);
			ChannelCount = 64;
		}
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in _schedule)
				yield return new RawPatternStep.Emit(note);
		}
	}

	private sealed class AdHocResolver(
		PreparedRoslynIncrementalScriptSources scripts, AdHocPattern transient)
		: IIncrementalInvocationResolver
	{
		public bool TryResolve(ObjectId id, out SongObject? source)
		{
			if (id == transient.Id)
			{
				source = transient;
				return true;
			}
			return scripts.TryResolve(id, out source);
		}
	}

	/// <summary>Bind an instrument's tone graph for one musical note.
	/// No mutable, shared resolver state or eager audio is retained.</summary>
	private sealed class InstrumentToneSoundResolver(
		Func<ObjectId, ISound?> resolve) : ISoundResolver
	{
		public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
		{
			// An Instrument selects a single terminal voice: recursive
			// Pattern/Sequence tones are rendered as private mixdowns.
			sound = resolve(id);
			return sound is not null;
		}
	}

	/// <summary>Keep the exact sound/state/envelope snapshot selected while
	/// walking an Instrument tone graph at a particular event frame.</summary>
	private sealed class BoundInstrumentInvocationSound(
		SoundInvocation invocation) : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> invocation.Configuration;
		public SoundState CreateState() => invocation.State;
		public SoundInvocation CreateInvocation(
			double pitchMultiplier, double playbackSpeedMultiplier)
			=> invocation;
		public long? GetEndFrameExclusive(
			RenderContext context, SoundState state)
			=> invocation.Sound.GetEndFrameExclusive(context, state);
		public void Render(RenderContext context, SoundState state,
			long startFrame, int frameCount, Span<float> destination)
			=> invocation.Sound.Render(context, state, startFrame,
				frameCount, destination);
	}

	private sealed class TrackedMixdown(
		PreparedRecursiveMixdownSound sound, long startFrame,
		int physicalChannel, long physicalPlaybackOwner,
		uint? virtualChannelId, long? virtualOwner,
		IReadOnlyList<long>? enclosingSourceScopes)
	{
		public PreparedRecursiveMixdownSound Sound { get; } = sound;
		public long StartFrame { get; } = startFrame;
		public int PhysicalChannel { get; } = physicalChannel;
		public long PhysicalPlaybackOwner { get; } = physicalPlaybackOwner;
		public uint? VirtualChannelId { get; } = virtualChannelId;
		public long? VirtualOwner { get; } = virtualOwner;
		// An indirect Instrument leaf remains a child of every flattened
		// instigator, even when its independent renderer has a private clock.
		public IReadOnlyList<long> EnclosingSourceScopes { get; } =
			enclosingSourceScopes?.ToArray() ?? [];
		public NoteDisplacementAction Displacement { get; set; } =
			NoteDisplacementAction.Cut;
	}

	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _samples;

	public PreparedIncrementalPlaybackFactory(RenderConfiguration configuration)
		: this(configuration, new InMemorySampleDataProvider()) { }

	public PreparedIncrementalPlaybackFactory(
		RenderConfiguration configuration, ISampleDataProvider samples)
	{
		_configuration = configuration
			?? throw new ArgumentNullException(nameof(configuration));
		_samples = samples ?? throw new ArgumentNullException(nameof(samples));
	}

	public PreparedIncrementalPlaybackPlan Create(
		SongDocument document, ObjectId? rootSourceId = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		return Create(SongDocumentSnapshot.Create(document), rootSourceId);
	}

	public PreparedIncrementalPlaybackPlan Create(
		SongDocumentSnapshot snapshot, ObjectId? rootSourceId = null,
		int startOrder = 0, int? startRow = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null,
		bool repeatPattern = false)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Never give the producer or renderer the caller's mutable snapshot.
		// Cloning shares immutable PCM but captures editable source parameters.
		SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(snapshot.Document);
		ObjectId root = rootSourceId ?? frozen.Document.RootSequenceId;
		if (root.IsNone)
			throw new InvalidOperationException(
				"The prepared recursive source needs a root sequence or Pattern ID.");

		// Restricted Roslyn compilation happens here, before any playback.
		// The script resolver separately owns the same frozen definitions.
		PreparedRoslynIncrementalScriptSources scripts = new(frozen);
		if (!scripts.TryResolve(root, out SongObject? selected)
			|| selected is not (PatternDefinition or SequenceDefinition))
			throw new ArgumentException(
				"Root source must resolve to a prepared Pattern or Sequence.",
				nameof(rootSourceId));

		// Keep the renderer's object graph private too: the plan exposes its
		// descriptive snapshot, but callers may mutate that document. The
		// additional clone shares immutable PCM and does not decode assets.
		SongDocumentSnapshot rendererSnapshot =
			SongDocumentSnapshot.Create(frozen.Document);
		PlaybackSnapshotSoundResolver sounds =
			new(rendererSnapshot.Document, _samples);
		// Resolve every direct sample/synth/instrument and envelope now so
		// injected providers and codec-dependent work cannot run in Render.
		sounds.PrepareDirectSources();

		SequencingContext sequencingContext = new();
		IncrementalRecursiveTimeline timeline =
			scripts.CreateTimeline(sequencingContext);
		try
		{
			long invocationId = timeline.AddRoot(
				root, startOrder, startRow, shouldFollowOrderJump);
			PlaybackSession session = new(
				new RenderContext(_configuration),
				new NoteScheduleBuilder().Freeze(), sounds);
			PreparedIncrementalAudioSource source = CreatePrivateMixdownAwareSource(
				timeline, session, scripts, sounds, [root],
				out Action disposePrivateMixdowns,
				sequencingContext.Diagnostics,
				repeatSourceId: repeatPattern ? root : null,
				repeatStartRow: startRow);
			return new PreparedIncrementalPlaybackPlan(
				frozen, timeline, session, source, invocationId,
				disposePrivateMixdowns, sequencingContext);
		}
		catch
		{
			timeline.Dispose();
			throw;
		}
	}


	/// <summary>
	/// Realtime live-preview and row-audition schedules use the same coroutine
	/// renderer; only the supplied already-resolved NoteSchedule is transient.
	/// </summary>
	public PreparedIncrementalPlaybackPlan CreateAdHoc(
		SongDocumentSnapshot snapshot, NoteSchedule schedule)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(schedule);
		SongDocumentSnapshot frozen =
			SongDocumentSnapshot.Create(snapshot.Document);
		PreparedRoslynIncrementalScriptSources scripts = new(frozen);
		PlaybackSnapshotSoundResolver sounds = new(
			SongDocumentSnapshot.Create(frozen.Document).Document, _samples);
		sounds.PrepareDirectSources();
		AdHocPattern transient = new(frozen.Document.AllocateObjectId(), schedule);
		SequencingContext context = new();
		IncrementalRecursiveTimeline timeline =
			new(context, new AdHocResolver(scripts, transient), scripts);
		try
		{
			long invocation = timeline.AddRoot(transient.Id);
			PlaybackSession session = new(
				new RenderContext(_configuration),
				new NoteScheduleBuilder().Freeze(), sounds);
			PreparedIncrementalAudioSource source =
				CreatePrivateMixdownAwareSource(
					timeline, session, scripts, sounds, [transient.Id],
					out Action disposeChildren,
					context.Diagnostics);
			return new PreparedIncrementalPlaybackPlan(
				frozen, timeline, session, source,
				invocation, disposeChildren, context);
		}
		catch
		{
			timeline.Dispose();
			throw;
		}
	}


	/// <summary>
	/// Each physical mixdown start gets a new private recursive clock and
	/// renderer. The parent receives an invocation-unique sound ID.
	/// The same PCM worker advances each child generator and renderer
	/// synchronously. Rewinds reconstruct both; no prepared event journals,
	/// separate preparation threads or cooked PCM buffers are retained.
	/// </summary>
	private PreparedIncrementalAudioSource CreatePrivateMixdownAwareSource(
		IncrementalRecursiveTimeline timeline,
		PlaybackSession session,
		PreparedRoslynIncrementalScriptSources scripts,
		PlaybackSnapshotSoundResolver sounds,
		IReadOnlyList<ObjectId> ancestry,
		out Action disposePrivateMixdowns,
		SequencingDiagnosticLog diagnostics,
		bool isPrivateChild = false,
		double privateClockRate = 1.0,
		ObjectId? repeatSourceId = null,
		int? repeatStartRow = null)
	{
		// The same bounded diagnostic sink is shared by root and nested
		// private mixdowns. Never invoke the UI from audio rendering.
		session.ReplayRequiredSeekObserved =
			(effect, nativeFrame) =>
				diagnostics.ReportExpensiveSourceSeek(effect, nativeFrame);

		timeline.ReadRememberedNoteVolume = (owner, host, time) =>
			session.GetRememberedNoteVolume(host, owner,
				FrameTime.Ceiling(time, session.SampleRate));

		List<PreparedRecursiveMixdownSound> privateVoices = [];
		Dictionary<ObjectId, ISound> registeredVoices = [];
		Dictionary<(long Owner, int Host), TrackedMixdown> physicalVoices = [];
		Dictionary<(long Owner, uint Id), TrackedMixdown> scopedVoices = [];
		List<TrackedMixdown> displacedVoices = [];
		disposePrivateMixdowns = () =>
		{
			foreach (ObjectId id in registeredVoices.Keys)
				sounds.UnregisterPreparedMixdown(id);
			registeredVoices.Clear();
			timeline.ScopeCanceled -= CancelPrivateScope;
			foreach (PreparedRecursiveMixdownSound voice in privateVoices)
				voice.Dispose();
			privateVoices.Clear();
			physicalVoices.Clear();
			scopedVoices.Clear();
			displacedVoices.Clear();
		};

		// Run only after a PCM block has detached its finished rendered voices.
		// Release tails and displaced NNA voices remain present in the session,
		// while detached anti-click residue is independent of the sound.
		void RetireFinished()
		{
			foreach (var pair in registeredVoices.ToArray())
			{
				if (session.HasActiveSound(pair.Value))
					continue;
				sounds.UnregisterPreparedMixdown(pair.Key);
				registeredVoices.Remove(pair.Key);
			}
			for (int i = privateVoices.Count - 1; i >= 0; i--)
			{
				PreparedRecursiveMixdownSound voice = privateVoices[i];
				if (session.HasActiveSound(voice))
					continue;
				voice.Dispose();
				privateVoices.RemoveAt(i);
			}
			foreach (var pair in physicalVoices.ToArray())
				if (!session.HasActiveSound(pair.Value.Sound))
					physicalVoices.Remove(pair.Key);
			foreach (var pair in scopedVoices.ToArray())
				if (!session.HasActiveSound(pair.Value.Sound))
					scopedVoices.Remove(pair.Key);
			displacedVoices.RemoveAll(
				voice => !session.HasActiveSound(voice.Sound));
		}


		void Schedule(TrackedMixdown voice, long frame,
			NoteDisplacementAction action, bool newNoteDisplacement = false)
		{
			switch (action)
			{
				case NoteDisplacementAction.Cut:
					voice.Sound.ScheduleCut(frame);
					break;
				case NoteDisplacementAction.Continue:
					break;
				case NoteDisplacementAction.Off:
					voice.Sound.ScheduleRelease(frame);
					break;
				case NoteDisplacementAction.Fade:
					voice.Sound.ScheduleFade(frame, newNoteDisplacement);
					break;
			}
		}

		void ReplacePhysical(int channel, long playbackOwner, long frame)
		{
			if (!physicalVoices.Remove((playbackOwner, channel), out TrackedMixdown? old))
				return;
			Schedule(old, frame, old.Displacement,
				newNoteDisplacement: true);
			if (old.Displacement != NoteDisplacementAction.Cut)
				displacedVoices.Add(old);
		}

		void ReplaceVirtual(long owner, uint id, long frame)
		{
			if (!scopedVoices.Remove((owner, id), out TrackedMixdown? old))
				return;
			Schedule(old, frame, old.Displacement,
				newNoteDisplacement: true);
			if (old.Displacement != NoteDisplacementAction.Cut)
				displacedVoices.Add(old);
		}

		void ApplyPrivateScopeControl(long scopeId, long frame,
			NoteDisplacementAction action)
		{
			if (action == NoteDisplacementAction.Continue)
				return;
			// A flattened source is a *logical note*: Off/Cut/Fade must
			// reach private renderers hosted by any descendant, including
			// a tone selected through several Instrument layers and a
			// private note displaced by NNA. Physical host indices are
			// not sufficient to express that ownership.
			TrackedMixdown[] affected = physicalVoices.Values
				.Concat(scopedVoices.Values).Concat(displacedVoices)
				.Where(v => v.EnclosingSourceScopes.Contains(scopeId))
				.Distinct().ToArray();
			foreach (TrackedMixdown voice in affected)
				Schedule(voice, frame, action,
					newNoteDisplacement: true);
			if (action == NoteDisplacementAction.Cut)
			{
				foreach (var pair in physicalVoices.ToArray())
					if (pair.Value.EnclosingSourceScopes.Contains(scopeId))
						physicalVoices.Remove(pair.Key);
				foreach (var pair in scopedVoices.ToArray())
					if (pair.Value.EnclosingSourceScopes.Contains(scopeId))
						scopedVoices.Remove(pair.Key);
				displacedVoices.RemoveAll(
					v => v.EnclosingSourceScopes.Contains(scopeId));
			}
		}

		void CancelPrivateScope(long scopeId)
			=> ApplyPrivateScopeControl(scopeId, session.NextFrame,
				NoteDisplacementAction.Cut);
		timeline.ScopeCanceled += CancelPrivateScope;

		void ProcessControl(ChannelTarget target, long owner,
			long playbackOwner, long frame, NoteCommand command)
		{
			if (command is ControlFlattenedSourceCommand sourceControl)
			{
				ApplyPrivateScopeControl(sourceControl.ChildScopeId,
					frame, sourceControl.Action);
				return;
			}
			if (target.Kind == ChannelTargetKind.Physical)
			{
				int channel = target.PhysicalChannel;
				if (command is ApplyPastNoteActionCommand past)
				{
					for (int p = displacedVoices.Count - 1; p >= 0; p--)
					{
						TrackedMixdown prior = displacedVoices[p];
						if (prior.VirtualChannelId.HasValue
							|| prior.PhysicalChannel != channel
							|| prior.PhysicalPlaybackOwner != playbackOwner)
							continue;
						NoteDisplacementAction action = past.Action switch
						{
							TrackerPastNoteAction.Cut => NoteDisplacementAction.Cut,
							TrackerPastNoteAction.Off => NoteDisplacementAction.Off,
							TrackerPastNoteAction.Fade => NoteDisplacementAction.Fade,
							_ => throw new InvalidOperationException(
								"Unsupported past-note lifecycle operation."),
						};
						Schedule(prior, frame, action);
						if (action == NoteDisplacementAction.Cut)
							displacedVoices.RemoveAt(p);
					}
				}
				if (!physicalVoices.TryGetValue((playbackOwner, channel),
					out TrackedMixdown? active))
					return;
				switch (command)
				{
					case NoteOffCommand:
						active.Sound.ScheduleRelease(frame);
						break;
					case NoteCutCommand:
						active.Sound.ScheduleCut(frame);
						physicalVoices.Remove((playbackOwner, channel));
						break;
					case SetCurrentVoiceDisplacementActionCommand nna:
						active.Displacement = nna.Action;
						break;
				}
				return;
			}

			if (target.Kind == ChannelTargetKind.Virtual)
			{
				var key = (owner, target.VirtualChannelId);
				if (command is ApplyPastNoteActionCommand past)
				{
					NoteDisplacementAction action = past.Action switch
					{
						TrackerPastNoteAction.Cut => NoteDisplacementAction.Cut,
						TrackerPastNoteAction.Off => NoteDisplacementAction.Off,
						TrackerPastNoteAction.Fade => NoteDisplacementAction.Fade,
						_ => throw new InvalidOperationException(
							"Unsupported virtual past-note lifecycle operation."),
					};
					for (int p = displacedVoices.Count - 1; p >= 0; p--)
					{
						TrackedMixdown prior = displacedVoices[p];
						if (prior.VirtualChannelId != target.VirtualChannelId
							|| prior.VirtualOwner != owner)
							continue;
						Schedule(prior, frame, action);
						if (action == NoteDisplacementAction.Cut)
							displacedVoices.RemoveAt(p);
					}
				}
				if (!scopedVoices.TryGetValue(key, out TrackedMixdown? active))
					return;
				switch (command)
				{
					case NoteOffCommand:
						active.Sound.ScheduleRelease(frame);
						break;
					case NoteCutCommand:
						active.Sound.ScheduleCut(frame);
						scopedVoices.Remove(key);
						break;
					case SetCurrentVoiceDisplacementActionCommand nna:
						active.Displacement = nna.Action;
						break;
				}
				return;
			}

			if ((target.Kind is ChannelTargetKind.AllVirtual
				or ChannelTargetKind.AllVirtualInScope)
				&& (command is NoteOffCommand or NoteCutCommand))
			{
				foreach (var pair in scopedVoices.ToArray())
				{
					if (target.Kind == ChannelTargetKind.AllVirtualInScope
						&& pair.Key.Owner != owner
						|| pair.Value.StartFrame >= frame)
						continue;
					if (command is NoteOffCommand)
						pair.Value.Sound.ScheduleRelease(frame);
					else
					{
						pair.Value.Sound.ScheduleCut(frame);
						scopedVoices.Remove(pair.Key);
					}
				}
				// AllVirtual includes migrated NNA voices, whereas
				// AllVirtualInScope intentionally does not.
				if (target.Kind == ChannelTargetKind.AllVirtual)
					for (int p = displacedVoices.Count - 1; p >= 0; p--)
					{
						TrackedMixdown previous = displacedVoices[p];
						if (previous.VirtualChannelId is null
							|| previous.StartFrame >= frame)
							continue;
						if (command is NoteOffCommand)
							previous.Sound.ScheduleRelease(frame);
						else
						{
							previous.Sound.ScheduleCut(frame);
							displacedVoices.RemoveAt(p);
						}
					}
			}
		}

		PreparedRecursiveMixdownSound CreatePrivateSound(
			ObjectId childSource, IReadOnlyList<ObjectId> path, long parentFrame)
		{
			if (path.Contains(childSource))
				throw new InvalidOperationException(
					$"Recursive source cycle includes object {childSource.Value}.");
			ObjectId[] childAncestry = new ObjectId[path.Count + 1];
			for (int i = 0; i < path.Count; i++)
				childAncestry[i] = path[i];
			childAncestry[^1] = childSource;

			PrivateRecursivePlayback CreateChildPlayback(
				double pitchMultiplier, double playbackSpeedMultiplier)
			{
				IncrementalRecursiveTimeline childTimeline =
					scripts.CreateTimeline(new SequencingContext(
						pitchMultiplier: pitchMultiplier,
						playbackSpeedMultiplier: playbackSpeedMultiplier,
						diagnostics: diagnostics));
				try
				{
					childTimeline.AddRoot(childSource);
					double effectiveTempo =
						SequencingConstants.DefaultTempo * playbackSpeedMultiplier;
					if (!double.IsFinite(effectiveTempo))
						throw new InvalidOperationException(
							"Private playback rate produced a non-finite Tempo.");
					PlaybackSession childSession = new(
						new RenderContext(_configuration),
						new NoteScheduleBuilder().Freeze(), sounds,
						initialTempo: effectiveTempo,
						applyFinalSpeakerFilters: false);
					PreparedIncrementalAudioSource childSourceStream =
						CreatePrivateMixdownAwareSource(
							childTimeline, childSession, scripts, sounds,
							childAncestry, out Action disposeDescendants,
							diagnostics,
							isPrivateChild: true,
							privateClockRate: playbackSpeedMultiplier);
					return new PrivateRecursivePlayback(childTimeline,
						childSession, childSourceStream, disposeDescendants);
				}
				catch
				{
					childTimeline.Dispose();
					throw;
				}
			}
			PreparedRecursiveMixdownSound voice = new(
				CreateChildPlayback(1.0, 1.0), parentFrame, CreateChildPlayback);
			privateVoices.Add(voice);
			return voice;
		}

		void Track(PreparedRecursiveMixdownSound sound,
			ChannelTarget target, long owner, long playbackOwner, long frame,
			IReadOnlyList<long>? enclosingSourceScopes)
		{
			TrackedMixdown tracked = new(sound, frame,
				target.Kind == ChannelTargetKind.Physical
					? target.PhysicalChannel : -1, playbackOwner,
				target.Kind == ChannelTargetKind.Virtual
					? target.VirtualChannelId : null,
				target.Kind == ChannelTargetKind.Virtual ? owner : null,
				enclosingSourceScopes);
			if (target.Kind == ChannelTargetKind.Physical)
				physicalVoices[(playbackOwner, target.PhysicalChannel)] = tracked;
			else if (target.Kind == ChannelTargetKind.Virtual)
				scopedVoices[(owner, target.VirtualChannelId)] = tracked;
		}

		// Ordinary sample/FM Instruments keep the existing immutable cached
		// resolver; only a tone chain actually selecting a Pattern/Sequence
		// requires a unique bound invocation/private recursive clock.
		bool SelectsRecursiveTone(ObjectId id, double pitch,
			IReadOnlyList<ObjectId> path)
		{
			if (!scripts.TryResolve(id, out SongObject? definition)
				|| definition is not InstrumentDefinition instrument)
				return false;
			if (path.Contains(id))
				throw new InvalidOperationException(
					$"Recursive instrument source cycle includes object {id.Value}.");
			ToneSpecification? tone = InstrumentSound.SelectTone(
				instrument, pitch);
			if (tone is null)
				return false;
			if (scripts.TryResolve(tone.SourceId, out SongObject? child)
				&& child is PatternDefinition or SequenceDefinition)
				return true;
			ObjectId[] nextPath = new ObjectId[path.Count + 1];
			for (int i = 0; i < path.Count; i++)
				nextPath[i] = path[i];
			nextPath[^1] = id;
		return SelectsRecursiveTone(tone.SourceId,
			checked(pitch * tone.PitchMultiplier), nextPath);
		}

		// Only a selected tone is visited, so unused instrument branches
		// cannot cause cycles or unnecessarily instantiate private timelines.
		ISound? ResolveTone(ObjectId id, IReadOnlyList<ObjectId> path,
			ChannelTarget target, long owner, long playbackOwner, long frame,
			IReadOnlyList<long>? enclosingSourceScopes)
		{
			if (scripts.TryResolve(id, out SongObject? definition))
			{
				if (definition is PatternDefinition or SequenceDefinition)
				{
					PreparedRecursiveMixdownSound voice =
						CreatePrivateSound(id, path, frame);
					Track(voice, target, owner, playbackOwner, frame,
						enclosingSourceScopes);
					return voice;
				}
				if (definition is InstrumentDefinition instrument)
				{
					if (path.Contains(id))
						throw new InvalidOperationException(
							$"Recursive instrument source cycle includes object {id.Value}.");
					ObjectId[] nextPath = new ObjectId[path.Count + 1];
					for (int p = 0; p < path.Count; p++)
						nextPath[p] = path[p];
					nextPath[^1] = id;
					return new InstrumentSound(instrument,
						new InstrumentToneSoundResolver(child =>
							ResolveTone(child, nextPath, target, owner, playbackOwner,
								frame, enclosingSourceScopes)),
						sounds);
				}
			}
			return sounds.TryResolve(id, mixdown: false, out ISound? direct)
				? direct : null;
		}

		NoteEvent Transform(NoteEvent note, long parentFrame, long owner)
		{
			NoteCommand[] commands = new NoteCommand[note.Commands.Count];
			for (int i = 0; i < commands.Length; i++)
			{
				NoteCommand command = note.Commands[i];
				if (command is not StartNoteCommand start)
				{
					ProcessControl(note.Target, owner,
						note.PhysicalPlaybackOwner, parentFrame, command);
					// Scale only renderer-facing Tempo, leaving the source
					// shared musical state and effect memory unchanged.
					commands[i] = command switch
					{
						SetTempoCommand tempo when privateClockRate != 1.0 =>
							new SetTempoCommand(tempo.TicksPerDiachron * privateClockRate),
						SetTempoRampCommand ramp when privateClockRate != 1.0 =>
							new SetTempoRampCommand(ramp.EndingTempo * privateClockRate,
								ramp.TrackerTicks),
						_ => command,
					};
					continue;
				}
				// An instrument's selected ToneSpecification may lead to
				// another instrument or a private Pattern/Sequence voice.
				// Bind the complete invocation now so its recursive leaf is
				// tracked before subsequent same-frame lifecycle commands.
				// A private parent's live pitch is sampled at this child-note
				// boundary for Instrument tone selection. The child voice's
				// continuous curve applies only later relative changes.
				double inheritedPitch =
					session.InheritedPitchAtFrame?.Invoke(parentFrame) ?? 1.0;
				if (!(inheritedPitch > 0.0) || !double.IsFinite(inheritedPitch))
					throw new InvalidOperationException(
						"Private pitch modulation must be positive and finite.");
				double selectedPitch = start.PitchMultiplier * inheritedPitch;
				if (scripts.TryResolve(start.SourceId, out SongObject? selected)
					&& selected is InstrumentDefinition
					&& SelectsRecursiveTone(start.SourceId,
						selectedPitch, ancestry))
				{
					if (note.Target.Kind == ChannelTargetKind.Physical)
						ReplacePhysical(note.Target.PhysicalChannel,
							note.PhysicalPlaybackOwner, parentFrame);
					else if (note.Target.Kind == ChannelTargetKind.Virtual)
					{
						ReplaceVirtual(owner, note.Target.VirtualChannelId,
							parentFrame);
					}
					ISound instrument = ResolveTone(start.SourceId, ancestry,
						note.Target, owner, note.PhysicalPlaybackOwner, parentFrame,
						start.ParentSourceScopes)!;
					SoundInvocation? bound = instrument.CreateInvocation(
						selectedPitch,
						start.PlaybackSpeedMultiplier * privateClockRate);
					if (bound is not null)
					{
						ObjectId boundId = sounds.RegisterPreparedMixdown(
							new BoundInstrumentInvocationSound(bound));
						registeredVoices.Add(boundId, bound.Sound);
						commands[i] = start with
						{
							SourceId = boundId, Mixdown = false,
							PitchMultiplier = 1, PlaybackSpeedMultiplier = 1,
						};
					}
					else
						commands[i] = start;
					continue;
				}
				bool nested = start.Mixdown
					&& scripts.TryResolve(start.SourceId, out SongObject? definition)
					&& definition is PatternDefinition or SequenceDefinition;
				if (!nested)
				{
					// An unresolved note does not displace the existing
					// renderer voice. Known direct sounds do.
					if (sounds.TryResolve(start.SourceId, start.Mixdown, out _))
					{
						if (note.Target.Kind == ChannelTargetKind.Physical)
							ReplacePhysical(note.Target.PhysicalChannel,
								note.PhysicalPlaybackOwner, parentFrame);
						else if (note.Target.Kind == ChannelTargetKind.Virtual)
						{
							ReplaceVirtual(owner, note.Target.VirtualChannelId,
								parentFrame);
						}
					}
					commands[i] = command;
					continue;
				}
				if (note.Target.Kind == ChannelTargetKind.Physical)
					ReplacePhysical(note.Target.PhysicalChannel,
						note.PhysicalPlaybackOwner, parentFrame);
				else if (note.Target.Kind == ChannelTargetKind.Virtual)
				{
					ReplaceVirtual(owner, note.Target.VirtualChannelId,
						parentFrame);
				}
 				PreparedRecursiveMixdownSound privateVoice =
					CreatePrivateSound(start.SourceId, ancestry, parentFrame);
				ObjectId preparedId = sounds.RegisterPreparedMixdown(privateVoice);
			registeredVoices.Add(preparedId, privateVoice);
				Track(privateVoice, note.Target, owner,
					note.PhysicalPlaybackOwner, parentFrame,
					start.ParentSourceScopes);
				commands[i] = start with
				{
					SourceId = preparedId,
					Mixdown = false,
					PlaybackSpeedMultiplier =
						start.PlaybackSpeedMultiplier * privateClockRate,
				};
			}
			return note with { Commands = commands };
		}

		return new PreparedIncrementalAudioSource(
			timeline, session, prepareEvent: Transform,
			endInputAtNaturalCompletion: isPrivateChild,
			repeatRoot: repeatSourceId is ObjectId repeated
				? () => timeline.AddRoot(repeated, startRow: repeatStartRow)
				: null,
			afterRender: RetireFinished);
	}
}
