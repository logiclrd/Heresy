using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

/// <summary>
/// Owns the experimental recursive timeline and adapter. The playback
/// session's tail/end-of-input policy stays explicit; disposing this plan
/// does not switch or affect production realtime/offline scheduling.
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
/// Opt-in preparation of a recursive Pattern/Sequence as a sample-accurate
/// PCM source, using stable song definitions and already decoded sample data.
/// This does not replace the production playback/export source factories.
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

	private sealed class TrackedMixdown(
		PreparedRecursiveMixdownSound sound, long startFrame,
		int physicalChannel)
	{
		public PreparedRecursiveMixdownSound Sound { get; } = sound;
		public long StartFrame { get; } = startFrame;
		public int PhysicalChannel { get; } = physicalChannel;
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
					out Action disposeChildren);
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
		bool isPrivateChild = false,
		ObjectId? repeatSourceId = null,
		int? repeatStartRow = null)
	{
		List<PreparedRecursiveMixdownSound> privateVoices = [];
		Dictionary<int, TrackedMixdown> physicalVoices = [];
		Dictionary<(long Owner, uint Id), TrackedMixdown> scopedVoices = [];
		List<TrackedMixdown> displacedVoices = [];
		disposePrivateMixdowns = () =>
		{
			foreach (PreparedRecursiveMixdownSound voice in privateVoices)
				voice.Dispose();
		};

		void Schedule(TrackedMixdown voice, long frame,
			NoteDisplacementAction action)
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
					voice.Sound.ScheduleFade(frame);
					break;
			}
		}

		void ReplacePhysical(int channel, long frame)
		{
			if (!physicalVoices.Remove(channel, out TrackedMixdown? old))
				return;
			Schedule(old, frame, old.Displacement);
			if (old.Displacement != NoteDisplacementAction.Cut)
				displacedVoices.Add(old);
		}

		void ProcessControl(ChannelTarget target, long owner,
			long frame, NoteCommand command)
		{
			if (target.Kind == ChannelTargetKind.Physical)
			{
				int channel = target.PhysicalChannel;
				if (command is ApplyPastNoteActionCommand past)
				{
					for (int p = displacedVoices.Count - 1; p >= 0; p--)
					{
						TrackedMixdown prior = displacedVoices[p];
						if (prior.PhysicalChannel != channel)
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
				if (!physicalVoices.TryGetValue(channel,
					out TrackedMixdown? active))
					return;
				switch (command)
				{
					case NoteOffCommand:
						active.Sound.ScheduleRelease(frame);
						break;
					case NoteCutCommand:
						active.Sound.ScheduleCut(frame);
						physicalVoices.Remove(channel);
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
				if (!scopedVoices.TryGetValue(key, out TrackedMixdown? active))
					return;
				if (command is NoteOffCommand)
					active.Sound.ScheduleRelease(frame);
				else if (command is NoteCutCommand)
				{
					active.Sound.ScheduleCut(frame);
					scopedVoices.Remove(key);
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
			}
		}

		NoteEvent Transform(NoteEvent note, long parentFrame, long owner)
		{
			NoteCommand[] commands = new NoteCommand[note.Commands.Count];
			for (int i = 0; i < commands.Length; i++)
			{
				NoteCommand command = note.Commands[i];
				if (command is not StartNoteCommand start)
				{
					ProcessControl(note.Target, owner, parentFrame, command);
					commands[i] = command;
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
							ReplacePhysical(note.Target.PhysicalChannel, parentFrame);
						else if (note.Target.Kind == ChannelTargetKind.Virtual)
						{
							var key = (owner, note.Target.VirtualChannelId);
							if (scopedVoices.Remove(key, out TrackedMixdown? prior))
								prior.Sound.ScheduleCut(parentFrame);
						}
					}
					commands[i] = command;
					continue;
				}
				if (note.Target.Kind == ChannelTargetKind.Physical)
					ReplacePhysical(note.Target.PhysicalChannel, parentFrame);
				else if (note.Target.Kind == ChannelTargetKind.Virtual)
				{
					var key = (owner, note.Target.VirtualChannelId);
					if (scopedVoices.Remove(key, out TrackedMixdown? prior))
						prior.Sound.ScheduleCut(parentFrame);
				}
				if (start.PitchMultiplier != 1.0
					|| start.PlaybackSpeedMultiplier != 1.0)
					throw new NotSupportedException(
						"Recursive mixdown transforms require private-clock remapping.");
				foreach (ObjectId ancestor in ancestry)
					if (ancestor == start.SourceId)
						throw new InvalidOperationException(
							$"Recursive mixdown source cycle includes object {start.SourceId.Value}.");

				ObjectId[] childAncestry = new ObjectId[ancestry.Count + 1];
				for (int a = 0; a < ancestry.Count; a++)
					childAncestry[a] = ancestry[a];
				childAncestry[^1] = start.SourceId;

				PrivateRecursivePlayback CreateChildPlayback()
				{
					IncrementalRecursiveTimeline childTimeline =
						scripts.CreateTimeline(new SequencingContext());
					try
					{
						childTimeline.AddRoot(start.SourceId);
						PlaybackSession childSession = new(
							new RenderContext(_configuration),
							new NoteScheduleBuilder().Freeze(), sounds);
						PreparedIncrementalAudioSource childSource =
							CreatePrivateMixdownAwareSource(
								childTimeline, childSession, scripts, sounds,
								childAncestry, out Action disposeDescendants,
								isPrivateChild: true);
						return new PrivateRecursivePlayback(
							childTimeline, childSession, childSource, disposeDescendants);
					}
					catch
					{
						childTimeline.Dispose();
						throw;
					}
				}
				PreparedRecursiveMixdownSound privateVoice = new(
					CreateChildPlayback(), parentFrame, CreateChildPlayback);
				ObjectId preparedId = sounds.RegisterPreparedMixdown(privateVoice);
				privateVoices.Add(privateVoice);
				TrackedMixdown tracked = new(privateVoice, parentFrame,
					note.Target.Kind == ChannelTargetKind.Physical
						? note.Target.PhysicalChannel : -1);
				if (note.Target.Kind == ChannelTargetKind.Physical)
					physicalVoices[note.Target.PhysicalChannel] = tracked;
				else if (note.Target.Kind == ChannelTargetKind.Virtual)
					scopedVoices[(owner, note.Target.VirtualChannelId)] = tracked;
				commands[i] = start with
				{
					SourceId = preparedId,
					Mixdown = false,
				};
			}
			return note with { Commands = commands };
		}

		return new PreparedIncrementalAudioSource(
			timeline, session, prepareEvent: Transform,
			endInputAtNaturalCompletion: isPrivateChild,
			repeatRoot: repeatSourceId is ObjectId repeated
				? () => timeline.AddRoot(repeated, startRow: repeatStartRow)
				: null);
	}
}
