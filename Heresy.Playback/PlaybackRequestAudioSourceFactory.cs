using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Heresy.Core.Diagnostics;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
using Heresy.Render.Realtime;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
using Heresy.Scripting.Analysis;

namespace Heresy.Playback;

public sealed class PlaybackSourceCompilationException : InvalidOperationException
{
	public PlaybackSourceCompilationException(
		string message, IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
		: base(message)
	{
		Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
	}
	public IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics { get; }
}

/// <summary>
/// The realtime transport now uses exactly the same coroutine recursion
/// as offline rendering. All script execution, timing and PCM synthesis
/// happen on the SDL-owned dedicated PCM worker, not during Create().
/// </summary>
public sealed class PlaybackRequestAudioSourceFactory :
	IBackgroundPlaybackSourceFactory,
	IPlaybackPositionTimelineProvider,
	IPlaybackRuntimeDiagnosticReportProvider,
	IPlaybackOlderAudioReportProvider,
	ILiveSynthSourcePreparationFactory
{
	// F6-only presentation metadata. Enqueue values, not per-row/per-call
	// closures, while the incremental cursor is looking ahead.
	private readonly record struct QueuedEditorPatternPosition(
		TimeSpan At,
		ObjectId PatternId,
		int Row,
		ObjectId? SequenceId,
		int? SequenceEntryIndex,
		SequenceCallsite? ActionCallsite);

	private readonly Func<RenderConfiguration> _configurationSource;
	private readonly ISampleDataProvider _samples;
	private readonly object _gate = new();
	private readonly Dictionary<PlaybackRequest, LiveRecursiveSource>
		_active = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<PlaybackRequest, PlaybackPositionTimeline>
		_positions = new(ReferenceEqualityComparer.Instance);

	public PlaybackRequestAudioSourceFactory(RenderConfiguration configuration)
		: this(configuration, new InMemorySampleDataProvider()) { }

	public PlaybackRequestAudioSourceFactory(RenderConfiguration configuration,
		ISampleDataProvider sampleDataProvider)
		: this(() => configuration
			?? throw new ArgumentNullException(nameof(configuration)),
			sampleDataProvider) { }

	/// <summary>Capture the current render configuration independently for
	/// each new request, after the transport has stopped the previous audio
	/// session. A change never mutates a running snapshot.</summary>
	public PlaybackRequestAudioSourceFactory(Func<RenderConfiguration> configurationSource)
		: this(configurationSource, new InMemorySampleDataProvider()) { }

	public PlaybackRequestAudioSourceFactory(
		Func<RenderConfiguration> configurationSource,
		ISampleDataProvider sampleDataProvider)
	{
		_configurationSource = configurationSource
			?? throw new ArgumentNullException(nameof(configurationSource));
		_samples = sampleDataProvider ?? throw new ArgumentNullException(nameof(sampleDataProvider));
	}

	/// <summary>Compile a replacement Synth from a detached song revision.
	/// Run only on the playback control worker, never the PCM producer or SDL
	/// callback. The immutable sound owns nested prepared resources.</summary>
	public ISound PrepareLiveSynthSource(
		SongDocumentSnapshot snapshot, ObjectId sourceId)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!snapshot.Document.TryGet(sourceId, out SongObject? node)
			|| node is not FmSynthDefinition)
			throw new ArgumentException(
				"Live Synth publication requires an existing Synth source.",
				nameof(sourceId));
		RenderConfiguration configuration = _configurationSource()
			?? throw new InvalidOperationException(
				"The playback output configuration source returned null.");
		// Replacement Synth generations are prepared for live editing.
		// They must retain headroom for subsequent audition revisions.
		PlaybackSnapshotSoundResolver sounds = new(
			SongDocumentSnapshot.Create(snapshot.Document).Document,
			_samples, configuration.SampleRate,
			reserveSynthMultiHeadroom: true);
		sounds.PrepareDirectSources();
		if (!sounds.TryResolve(sourceId, mixdown: false, out ISound? result)
			|| result is not FmSynthSound)
			throw new InvalidOperationException(
				"Could not prepare the requested Synth revision.");
		return result;
	}

	public IAudioOutputSource Create(PlaybackRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		RenderConfiguration configuration = _configurationSource()
			?? throw new InvalidOperationException(
				"The playback output configuration source returned null.");
		PreparedIncrementalPlaybackFactory factory = new(configuration, _samples);
		PreparedIncrementalPlaybackPlan plan;
		try
		{
			plan = request switch
			{
				SequencePlaybackRequest sequence => factory.Create(
					sequence.Snapshot, sequence.SequenceId,
					startOrder: sequence.StartPosition?.Order ?? 0,
					startRow: sequence.StartPosition?.Row,
					instrumentedSequenceId: sequence.EditorCallsiteSession.HasValue
						? sequence.SequenceId : null),
				PatternPlaybackRequest pattern => factory.Create(
					pattern.Snapshot, pattern.PatternId,
					startRow: pattern.StartRow,
					repeatPattern: pattern.Repeat,
					instrumentedPatternId: pattern.EditorCallsiteSession.HasValue
						? pattern.PatternId : null),
				AdHocPlaybackRequest adHoc =>
					factory.CreateAdHoc(adHoc.Snapshot, adHoc.Schedule,
						reserveSynthMultiHeadroom: adHoc.ReserveSynthMultiHeadroom),
				_ => throw new NotSupportedException(
					$"Unsupported playback request: {request.GetType().Name}"),
			};
		}
		catch (ArgumentException error)
		{
			throw new PlaybackSourceCompilationException(
				$"Could not prepare recursive playback: {error.Message}",
				[new ScriptAnalysisDiagnostic(
					"HRS3001", ScriptDiagnosticSeverity.Error,
					error.Message, new ScriptSourceSpan(0, 0))]);
		}
		PlaybackPositionTimeline? positions = request is AdHocPlaybackRequest
			? null : new PlaybackPositionTimeline(request switch
			{
				PatternPlaybackRequest pattern => pattern.EditorCallsiteSession,
				SequencePlaybackRequest sequence => sequence.EditorCallsiteSession,
				_ => null,
			});
		// Only editor-traced Script Patterns install this callback. Row
		// boundaries and scheduled actions must inherit the latest *actual*
		// voice snapshot rather than temporarily clearing the blue overlay.
		Action<HashSet<SequenceCallsite>, TimeSpan>? updateActiveEditorNotes = null;
		Func<long?>? nextEditorPositionBoundaryFrame = null;
		if (positions is not null)
		{
			if (request is SequencePlaybackRequest
				{ EditorCallsiteSession: long session })
			{
				// Keep source metadata with the actual order's row
				// activation, not the earlier Roslyn Play evaluation.
				plan.Timeline.EditorRowBegan +=
					(id, row, sequence, order, at, callsite) =>
						positions.Append(new PlaybackPositionTimelineEntry(at,
							new PlaybackPatternPosition(id, row, sequence, order)
							{
								EditorCallsiteSession =
									positions.EditorCallsiteTracingEnabled ? session : null,
								EditorPlayCallsite =
									positions.EditorCallsiteTracingEnabled ? callsite : null,
							}));
			}
			else if (request is PatternPlaybackRequest
				{ EditorCallsiteSession: long patternSession } patternTrace)
			{
				// Preserve all actions, not only the last emission on a row.
				// RowBegan is called again when a repeating Pattern loops;
				// no previous-cycle callsite must leak into the next cycle.
				List<SequenceCallsite> activeActions = [];
				// Published arrays are immutable; repeated activations from the
				// same authored callsite must not clone unchanged snapshots.
				SequenceCallsite[] actionSnapshot = [];
				SequenceCallsite[] activeNotes = [];
				int? activeRow = null;

				// Incremental cursor lookahead may report a future row or
				// delayed authored action before the PCM head reaches it.
				// Publishing that future timestamp immediately causes a
				// later post-event blue-note snapshot (at an earlier frame)
				// to violate the streamed timeline's chronological contract.
				// Keep only editor position changes pending; never defer the
				// audio events or alter their musical scheduling.
				PriorityQueue<QueuedEditorPatternPosition,
					(long Ticks, long Order)> pendingPositions = new();
				long nextPositionOrder = 0;
				// The first future due row acts as a soft render boundary.
				// Do not let a large PCM buffer accumulate all its silent rows.
				long nextPendingPositionFrame = long.MaxValue;
				int traceSampleRate = plan.Source.Format.SampleRate;
				void QueuePosition(QueuedEditorPatternPosition position)
				{
					// After leaving the Script editor, keep only musical
					// row-position tracking. No new authored action metadata.
					if (position.ActionCallsite is not null
						&& !positions.EditorCallsiteTracingEnabled)
						return;
					pendingPositions.Enqueue(position,
						(position.At.Ticks, nextPositionOrder++));
					long dueFrame = FrameTime.Ceiling(position.At, traceSampleRate);
					if (dueFrame > plan.Source.NextFrame
						&& dueFrame < nextPendingPositionFrame)
						nextPendingPositionFrame = dueFrame;
					// F6-only diagnostic; capture the burst/lookahead peak
					// before any due events are flushed. No retained journal.
					positions.RecordEditorPendingPositionCount(pendingPositions.Count);
				}
				void PublishPositionsThrough(TimeSpan when)
				{
					while (pendingPositions.TryPeek(out _, out var key)
						&& key.Ticks <= when.Ticks)
					{
						QueuedEditorPatternPosition pending =
							pendingPositions.Dequeue();
						if (!positions.EditorCallsiteTracingEnabled)
						{
							// A due row still updates transport position after the
							// editor has gone away; previously queued F6 action
							// spans are discarded without touching musical events.
							if (pending.ActionCallsite is null)
								positions.Append(new PlaybackPositionTimelineEntry(
									pending.At,
									new PlaybackPatternPosition(pending.PatternId,
										pending.Row, pending.SequenceId,
										pending.SequenceEntryIndex)));
							continue;
						}
						if (pending.ActionCallsite is { } callsite)
						{
							// Delayed authored actions activate on their
							// actual PCM deadline, potentially on a later row.
							int displayRow = activeRow ?? pending.Row;
							activeRow ??= displayRow;
							if (!activeActions.Contains(callsite))
							{
								activeActions.Add(callsite);
								actionSnapshot = activeActions.ToArray();
							}
							// Preserve an entry for every due activation, including
							// repeats from loops, but share the frozen membership
							// array with earlier entries when nothing has changed.
							positions.Append(new PlaybackPositionTimelineEntry(
								pending.At,
								new PlaybackPatternPosition(pending.PatternId,
									displayRow, null, null)
								{
									EditorCallsiteSession = patternSession,
									EditorPatternCallsite = callsite,
									EditorPatternCallsites = actionSnapshot,
									EditorActiveNoteCallsites = activeNotes,
								}));
						}
						else
						{
							activeRow = pending.Row;
							activeActions.Clear();
							actionSnapshot = [];
							positions.Append(new PlaybackPositionTimelineEntry(
								pending.At,
								new PlaybackPatternPosition(pending.PatternId,
									pending.Row, pending.SequenceId,
									pending.SequenceEntryIndex)
								{
									EditorCallsiteSession = patternSession,
									EditorActiveNoteCallsites = activeNotes,
								}));
						}
					}
					// The earliest entry has either been published or is
					// still due at this PCM boundary. Further queued rows
					// can establish their own future boundary on enqueue.
					nextPendingPositionFrame = long.MaxValue;
					if (pendingPositions.TryPeek(out _, out var nextPriority))
					{
						long frame = FrameTime.Ceiling(
							TimeSpan.FromTicks(nextPriority.Ticks), traceSampleRate);
						if (frame > plan.Source.NextFrame)
							nextPendingPositionFrame = frame;
					}
				}
				nextEditorPositionBoundaryFrame = () =>
					nextPendingPositionFrame == long.MaxValue
						? null : nextPendingPositionFrame;
				updateActiveEditorNotes = (next, at) =>
				{
					// Row/action metadata is visible only once the PCM clock
					// reaches it, and always before the voice-state update
					// at the same sample-frame boundary.
					PublishPositionsThrough(at);
					if (!positions.EditorCallsiteTracingEnabled)
						return;
					// Only a changed voice set allocates a frozen array and
					// appends a new position. Row/action entries share it.
					if (activeNotes.Length == next.Count
						&& next.SetEquals(activeNotes))
						return;
					activeNotes = next.Count == 0
						? [] : next.OrderBy(x => x.Start).ToArray();
					PlaybackPatternPosition? current = positions.GetPositionAt(at);
					if (current?.PatternId == patternTrace.PatternId)
						positions.Append(new PlaybackPositionTimelineEntry(at,
							current with { EditorActiveNoteCallsites = activeNotes }));
				};
				plan.Timeline.RowBegan += (id, row, sequence, order, at) =>
				{
					if (id != patternTrace.PatternId)
						return;
					QueuePosition(new QueuedEditorPatternPosition(
						at, id, row, sequence, order, null));
				};
				plan.Timeline.EditorPatternActionBegan += (id, row, at, callsite) =>
				{
					if (id != patternTrace.PatternId)
						return;
					QueuePosition(new QueuedEditorPatternPosition(
						at, id, row, null, null, callsite));
				};
			}
			else
			{
				plan.Timeline.RowBegan += (id, row, sequence, order, at) =>
					positions.Append(new PlaybackPositionTimelineEntry(at,
						new PlaybackPatternPosition(id, row, sequence, order)));
			}
		}
		if (updateActiveEditorNotes is not null)
		{
			// One scratch set per traced producer, reused at every exact
			// sample-frame boundary. The update callback publishes a new
			// immutable array only when the origins actually change.
			// Ordinary playback never installs this observer or set.
			HashSet<SequenceCallsite> scratch = [];
			plan.Source.SetEditorTraceBoundary(frame =>
			{
				if (positions?.EditorCallsiteTracingEnabled == true)
					plan.Source.Session.CollectActiveEditorNoteCallsites(scratch);
				// The producer still flushes due plain row positions after
				// the originating editor is gone, but it neither collects
				// expensive blue-voice snapshots nor records green spans.
				updateActiveEditorNotes(scratch, TimeSpan.FromSeconds(
					frame / (double)plan.Source.Format.SampleRate));
			}, () => positions?.EditorCallsiteTracingEnabled == true,
				nextEditorPositionBoundaryFrame);
		}
		LiveRecursiveSource live = new(plan, positions);
		lock (_gate)
		{
			_active.Clear();
			_active[request] = live;
			_positions.Clear();
			if (positions is not null)
				_positions[request] = positions;
		}
		return live;
	}

	public bool TryTakePlaybackPositionTimeline(PlaybackRequest request,
		out PlaybackPositionTimeline? timeline)
	{
		lock (_gate)
			return _positions.Remove(request, out timeline);
	}

	/// <summary>Read a single producer-published count, not its mutable
	/// collection of PlaybackVoice objects, from the transport poll thread.</summary>
	public bool TryGetOlderAudioVoiceCount(PlaybackRequest request, out int count)
	{
		lock (_gate)
		{
			if (_active.TryGetValue(request, out LiveRecursiveSource? live))
			{
				count = live.OlderAudioVoiceCount;
				return true;
			}
		}
		count = 0;
		return false;
	}

	public bool TryTakeRuntimeDiagnostics(PlaybackRequest request,
		out SequencingDiagnostic[] diagnostics)
	{
		LiveRecursiveSource? live;
		lock (_gate)
			_active.TryGetValue(request, out live);
		if (live is not null)
		{
			diagnostics = live.DrainRuntimeDiagnostics();
			return diagnostics.Length > 0;
		}
		diagnostics = [];
		return false;
	}

	private sealed class LiveRecursiveSource(
		PreparedIncrementalPlaybackPlan plan,
		PlaybackPositionTimeline? positions) : ILiveAudioOutputSource, ILiveSynthSourceRevisionOutput,
		IFirPreparationAudioSource, IDisposable
	{
		// Publication and note messages share FIFO ordering on the producer.
		private abstract record LiveAction;
		private sealed record NoteAction(LivePlaybackEvent Event) : LiveAction;
		private sealed record SynthAction(ObjectId Id, ISound Sound,
			bool ApplyToExistingVoices) : LiveAction;
		private readonly ConcurrentQueue<LiveAction> _commands = new();
		private readonly ConcurrentQueue<SequencingDiagnostic> _warnings = new();
		private int _olderAudioVoiceCount;
		public int OlderAudioVoiceCount => Volatile.Read(ref _olderAudioVoiceCount);
		public SequencingDiagnostic[] DrainRuntimeDiagnostics()
		{
			List<SequencingDiagnostic> messages = [];
			while (_warnings.TryDequeue(out SequencingDiagnostic? next))
				messages.Add(next);
			return [.. messages];
		}
		public AudioOutputFormat Format => plan.Source.Format;

        public void AdoptPreparedFirControls()
            => plan.Source.Session.AdoptPreparedFirControls();

        public void QueuePendingFirControls(FirPreparationScheduler scheduler)
            => plan.Source.Session.QueuePendingFirControls(scheduler);

		public void EnqueueLiveEvent(ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
		{
			ArgumentNullException.ThrowIfNull(commands);
			_commands.Enqueue(new NoteAction(
				new LivePlaybackEvent(target, commands.ToArray())));
		}
		public void EnqueueLiveSynthSource(ObjectId sourceId, ISound preparedSound)
		{
			ArgumentNullException.ThrowIfNull(preparedSound);
			_commands.Enqueue(new SynthAction(sourceId, preparedSound, false));
		}
		public void EnqueueLiveSynthParameters(ObjectId sourceId, ISound preparedSound)
		{
			ArgumentNullException.ThrowIfNull(preparedSound);
			_commands.Enqueue(new SynthAction(sourceId, preparedSound, true));
		}
		public void Render(int frameCount, Span<float> destination)
		{
			while (_commands.TryDequeue(out LiveAction? action))
			{
				switch (action)
				{
					case NoteAction note:
						plan.Source.ApplyLiveEvent(
							note.Event.Target, note.Event.Commands);
						break;
					case SynthAction synth:
						plan.AdoptPreparedLiveSynth(synth.Id, synth.Sound,
							synth.ApplyToExistingVoices);
						break;
				}
			}
			plan.Source.Render(frameCount, destination);
			// The note list is owned by this producer. Publish only the
			// scalar aggregate, never expose mutable PCM-owned voice objects.
			Volatile.Write(ref _olderAudioVoiceCount,
				plan.Source.Session.CountOlderAudioVoices());
			// Drain in the same worker that owns sequencing diagnostics.
			// Transport/UI readers see only immutable queued records.
			foreach (SequencingDiagnostic message
				in plan.SequencingContext.Diagnostics.Drain())
				_warnings.Enqueue(message);
			if (plan.Source.IsComplete && positions is not null)
				positions.Finish(plan.Source.LogicalDuration);
		}
		public void Dispose() => plan.Dispose();
	}
}
