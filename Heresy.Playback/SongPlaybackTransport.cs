using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

public interface ISongPlaybackTransport
	: IDisposable
{
	Task PlaySongAsync(
		SongDocument document);

	Task PlaySequenceAsync(
		SongDocument document,
		ObjectId sequenceId,
		SequencePlaybackPosition? startPosition = null);

	Task PlayPatternAsync(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		bool repeat = false);

	Task PlayAdHocAsync(
		SongDocument document,
		NoteSchedule schedule);

	Task SendLiveEventAsync(
		SongDocument document,
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands);

	Task StopAsync();
}

/// <summary>Optional committed Synth editor update. No playback session is
/// created if no audition is already active.</summary>
public interface ILiveSynthEditTransport
{
	Task PublishSynthEditAsync(SongDocument document, ObjectId sourceId);

	/// <summary>Publish all Synths referencing a successfully edited shared
	/// Envelope/Filter, then their transitive Synth Source parents.</summary>
	Task PublishSharedObjectEditAsync(SongDocument document, ObjectId objectId);
}

public interface IPlaybackPositionTransport
{
	event EventHandler<PlaybackPositionChangedEventArgs>? PlaybackPositionChanged;

	PlaybackPatternPosition? CurrentPlaybackPosition { get; }
}

public sealed class SongPlaybackTransport
	: ISongPlaybackTransport,
		IPlaybackPositionTransport,
		IPlaybackRuntimeDiagnosticsTransport,
		IPlaybackAudioHealthTransport,
		IPlaybackSnapshotTransport,
		ILiveSynthEditTransport
{
	private readonly BackgroundPlaybackController _controller;
	private readonly IBackgroundPlaybackSourceFactory _sourceFactory;
	private readonly SemaphoreSlim _commandGate = new(1, 1);
	private readonly object _positionGate = new();
	private readonly Stopwatch _positionStopwatch = new();
	private PlaybackSnapshotInfo _currentPlaybackSnapshot = new(0, null, 0);
	private long _lastAudioSessionId = -1;
	private long _lastAudioUnderruns;
	private bool _lastAudioFaultReported;
	private readonly Timer _positionTimer;

	private PlaybackPositionTimeline? _positionTimeline;
	private PlaybackRequest? _diagnosticRequest;
	private PlaybackPatternPosition? _currentPlaybackPosition;
	private bool _liveAuditionActive;
	private SongDocument? _liveAuditionDocument;
	private uint _liveAuditionAudioRevision;
	// Do not advance the session's frozen base revision for a partial
	// Synth publication: other direct Sounds still use that snapshot.
	private readonly Dictionary<ObjectId, uint> _preparedSynthRevisions = [];

	public SongPlaybackTransport(
		IAudioOutputBackend backend,
		IBackgroundPlaybackSourceFactory sourceFactory)
	{
		_sourceFactory =
			sourceFactory
				?? throw new ArgumentNullException(nameof(sourceFactory));
		_controller =
			new BackgroundPlaybackController(
				backend
					?? throw new ArgumentNullException(nameof(backend)),
				_sourceFactory);
		_positionTimer =
			new Timer(
				_ =>
				{
					PollPlaybackPosition();
					PollRuntimeDiagnostics();
					PollAudioHealth();
				},
				null,
				Timeout.Infinite,
				Timeout.Infinite);
	}

	public event EventHandler<PlaybackPositionChangedEventArgs>?
		PlaybackPositionChanged;
	public event EventHandler<PlaybackRuntimeDiagnosticsEventArgs>?
		RuntimeDiagnostics;

	public event EventHandler<PlaybackAudioHealthChangedEventArgs>?
		AudioHealthChanged;
	public event EventHandler<PlaybackSnapshotChangedEventArgs>?
		PlaybackSnapshotChanged;

	public PlaybackSnapshotInfo CurrentPlaybackSnapshot
	{
		get
		{
			lock (_positionGate)
				return _currentPlaybackSnapshot;
		}
	}

	public PlaybackPatternPosition? CurrentPlaybackPosition
	{
		get
		{
			lock (_positionGate)
				return _currentPlaybackPosition;
		}
	}

	public Task PlaySongAsync(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.RootSequenceId.IsNone)
		{
			throw new InvalidOperationException(
				"The song does not have a root sequence.");
		}

		return PlaySequenceAsync(
			document,
			document.RootSequenceId);
	}

	public Task PlaySequenceAsync(
		SongDocument document,
		ObjectId sequenceId,
		SequencePlaybackPosition? startPosition = null)
		=> PlayRequestAsync(
			SequencePlaybackRequest.Create(
				document,
				sequenceId,
				startPosition),
			document);

	public Task PlayPatternAsync(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		bool repeat = false)
		=> PlayRequestAsync(
			PatternPlaybackRequest.Create(
				document,
				patternId,
				startRow,
				repeat),
			document);

	public Task PlayAdHocAsync(
		SongDocument document,
		NoteSchedule schedule)
		=> PlayRequestAsync(
			AdHocPlaybackRequest.Create(
				document,
				schedule),
			document);

	public async Task SendLiveEventAsync(
		SongDocument document,
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(commands);

		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			StopPositionTracking(keepAudioMonitoring: true);
			await EnsureLiveAuditionCoreAsync(document, commands)
				.ConfigureAwait(false);
			await _controller.SendLiveEventAsync(
					target,
					commands)
				.ConfigureAwait(false);
		}
		catch
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			_preparedSynthRevisions.Clear();
			throw;
		}
		finally
		{
			_commandGate.Release();
		}
	}

	/// <summary>Prepare and publish a committed Synth edit without waiting
	/// for a new NoteOn, including its transitive Synth Source parents.</summary>
	public Task PublishSynthEditAsync(SongDocument document, ObjectId sourceId)
		=> PublishAffectedSynthEditsAsync(document, sourceId);

	/// <summary>Notify every Synth that references this shared Envelope or
	/// Filter. No playback is opened if no live audition is active.</summary>
	public Task PublishSharedObjectEditAsync(SongDocument document, ObjectId objectId)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (!document.TryGet(objectId, out SongObject? shared)
			|| shared is not (EnvelopeDefinition or FilterDefinition))
			return Task.CompletedTask;
		return PublishAffectedSynthEditsAsync(document, objectId);
	}

	private async Task PublishAffectedSynthEditsAsync(
		SongDocument document, ObjectId sourceId)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (sourceId.IsNone)
			throw new ArgumentOutOfRangeException(nameof(sourceId));
		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (!_liveAuditionActive
				|| !ReferenceEquals(_liveAuditionDocument, document)
				|| !_controller.SupportsLiveSynthPublication)
				return;
			// A changed Synth, shared Filter or shared Envelope can affect
			// several Synth definitions. Resolve all direct dependencies plus
			// transitive Source parents from one detached revision, then publish
			// them in child-before-parent order through the ordered live queue.
			IReadOnlyList<ObjectId> affected =
				GetDependentSynths(document, sourceId);
			SongDocumentSnapshot? snapshot = null;
			foreach (ObjectId affectedId in affected)
			{
				if (_preparedSynthRevisions.TryGetValue(affectedId,
					out uint revision)
					&& revision == document.AudioRevision)
					continue;
				snapshot ??= SongDocumentSnapshot.Create(document);
				await _controller.PublishLiveSynthParametersAsync(
						snapshot, affectedId).ConfigureAwait(false);
				_preparedSynthRevisions[affectedId] = snapshot.AudioRevision;
			}
		}
		finally
		{
			_commandGate.Release();
		}
	}

	/// <summary>Follow reverse Source, shared Envelope and Filter edges
	/// through Synths and *both* Instrument kinds. Instruments are
	/// intermediate dependents, not publishable Synth definitions; every
	/// declared tone is inspected, including ones not selected by the
	/// current held note. Only Synths actually reachable from the edit
	/// are published. All graph discovery and ordering is on the command
	/// thread, never on PCM.</summary>
	private static IReadOnlyList<ObjectId> GetDependentSynths(
		SongDocument document, ObjectId changedSourceId)
	{
		List<ObjectId> discoveredSynths = [];
		List<ObjectId> frontier = [changedSourceId];
		HashSet<ObjectId> reached = [changedSourceId];
		if (document.TryGet(changedSourceId, out SongObject? changed)
			&& changed is FmSynthDefinition)
			discoveredSynths.Add(changedSourceId);

		for (int i = 0; i < frontier.Count; i++)
		{
			ObjectId affected = frontier[i];
			foreach (SongObject candidate in document.Objects.Values)
			{
				if (reached.Contains(candidate.Id))
					continue;
				bool dependsOnAffected = candidate switch
				{
					FmSynthDefinition synth => SynthReferences(synth, affected),
					InstrumentDefinition instrument =>
						ToneReferences(instrument.ToneSpecifications, affected),
					ScriptInstrumentDefinition instrument =>
						ToneReferences(instrument.ToneSpecifications, affected),
					_ => false,
				};
				if (!dependsOnAffected)
					continue;
				reached.Add(candidate.Id);
				frontier.Add(candidate.Id);
				if (candidate is FmSynthDefinition)
					discoveredSynths.Add(candidate.Id);
			}
		}

		// The same parent Synth may refer directly to the edited object
		// AND indirectly through a nested Instrument/Synth. Discovery
		// order alone would then publish that parent before its child.
		// Walk only reached Source dependencies before emitting each Synth,
		// so a parent's frozen replacement always follows any affected
		// child revision it may resolve. Never publish Instruments.
		List<ObjectId> ordered = [];
		HashSet<ObjectId> inspected = [];
		foreach (ObjectId synthId in discoveredSynths)
			AppendDependenciesFirst(synthId);
		return ordered;

		void AppendDependenciesFirst(ObjectId id)
		{
			if (!reached.Contains(id) || !inspected.Add(id)
				|| !document.TryGet(id, out SongObject? obj))
				return;
			switch (obj)
			{
				case FmSynthDefinition synth:
					foreach (FmSynthNode node in synth.Graph.Nodes)
						if (node is FmSoundSourceNode source)
							AppendDependenciesFirst(source.SourceId);
					ordered.Add(synth.Id);
					break;
				case InstrumentDefinition instrument:
					foreach (ToneSpecification tone in instrument.ToneSpecifications)
						AppendDependenciesFirst(tone.SourceId);
					break;
				case ScriptInstrumentDefinition instrument:
					foreach (ToneSpecification tone in instrument.ToneSpecifications)
						AppendDependenciesFirst(tone.SourceId);
					break;
			}
		}

		static bool SynthReferences(FmSynthDefinition synth, ObjectId affected)
		{
			foreach (FmSynthNode node in synth.Graph.Nodes)
			{
				if (node is FmSoundSourceNode source
						&& source.SourceId == affected
					|| node is FmEnvelopeNode envelope
						&& envelope.EnvelopeId == affected
					|| node is FmFilterNode filter
						&& filter.Configuration?.DefinitionId == affected)
					return true;
			}
			return false;
		}

		static bool ToneReferences(
			IEnumerable<ToneSpecification> tones, ObjectId affected)
		{
			foreach (ToneSpecification tone in tones)
				if (tone.SourceId == affected
					|| tone.VolumeEnvelopeId == affected
					|| tone.PitchEnvelopeId == affected
					|| tone.PanningEnvelopeId == affected
					|| tone.FilterEnvelopeId == affected
					|| tone.FilterConfiguration?.DefinitionId == affected)
					return true;
			return false;
		}
	}

	public async Task StopAsync()
	{
		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			_preparedSynthRevisions.Clear();
			StopPositionTracking();
			await _controller.StopAsync().ConfigureAwait(false);
			PollAudioHealth();
			PublishPlaybackSnapshot(null, 0);
		}
		finally
		{
			_commandGate.Release();
		}
	}

	private async Task PlayRequestAsync(
		PlaybackRequest request,
		SongDocument sourceDocument)
	{
		ArgumentNullException.ThrowIfNull(request);

		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			_preparedSynthRevisions.Clear();
			StopPositionTracking();
			await _controller.PlayAsync(request).ConfigureAwait(false);
			PublishPlaybackSnapshot(sourceDocument, request.Snapshot.AudioRevision);
			PollAudioHealth();

			lock (_positionGate)
				_diagnosticRequest = request;
			PollRuntimeDiagnostics();

			if (_sourceFactory is IPlaybackPositionTimelineProvider provider
				&& provider.TryTakePlaybackPositionTimeline(
					request,
					out PlaybackPositionTimeline? timeline)
				&& timeline is not null)
			{
				StartPositionTracking(timeline);
			}
			else
				_positionTimer.Change(dueTime: 5, period: 20);
		}
		catch
		{
			StopPositionTracking();
			PublishPlaybackSnapshot(null, 0);
			throw;
		}
		finally
		{
			_commandGate.Release();
		}
	}

	private async Task EnsureLiveAuditionCoreAsync(
		SongDocument document,
		IReadOnlyList<NoteCommand> commands)
	{
		bool newNote = false;
		foreach (NoteCommand command in commands)
		{
			if (command is StartNoteCommand)
			{
				newNote = true;
				break;
			}
		}

		// A held note must be released in its existing session even when
		// an edit has changed the document meanwhile. The NEXT note start
		// reconstructs the playback snapshot from the current audio model.
		if (_liveAuditionActive
			&& (!newNote
				|| (ReferenceEquals(_liveAuditionDocument, document)
					&& _liveAuditionAudioRevision == document.AudioRevision)))
		{
			return;
		}

		// A direct Synth NoteOn can use a newer prepared graph without
		// tearing down the running session. Previously started voices
		// retain their existing ISound/SoundState; new starts resolve through
		// an audio-boundary-published overlay. Other sources still take the
		// established conservative full-session refresh path.
		if (_liveAuditionActive
			&& ReferenceEquals(_liveAuditionDocument, document)
			&& _controller.SupportsLiveSynthPublication
			&& TryGetDirectSynthStarts(document, commands,
				out IReadOnlyList<ObjectId> synthStarts))
		{
			SongDocumentSnapshot? snapshot = null;
			foreach (ObjectId sourceId in synthStarts)
			{
				if (_preparedSynthRevisions.TryGetValue(sourceId,
					out uint publishedRevision)
					&& publishedRevision == document.AudioRevision)
					continue;
				snapshot ??= SongDocumentSnapshot.Create(document);
				await _controller.PublishLiveSynthAsync(snapshot, sourceId)
					.ConfigureAwait(false);
				_preparedSynthRevisions[sourceId] = snapshot.AudioRevision;
			}
			return;
		}

		try
		{
			PlaybackRequest request = AdHocPlaybackRequest.Create(
				document, new NoteScheduleBuilder().Freeze(),
				reserveSynthMultiHeadroom: true);
			await _controller.PlayAsync(request).ConfigureAwait(false);
			PublishPlaybackSnapshot(document, request.Snapshot.AudioRevision);
			lock (_positionGate)
				_diagnosticRequest = request;
			_positionTimer.Change(dueTime: 5, period: 50);
			PollAudioHealth();
			_liveAuditionActive = true;
			_liveAuditionDocument = document;
			_liveAuditionAudioRevision = request.Snapshot.AudioRevision;
			_preparedSynthRevisions.Clear();
		}
		catch
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			_preparedSynthRevisions.Clear();
			PublishPlaybackSnapshot(null, 0);
			throw;
		}
	}

	/// <summary>Only allow direct Synth NoteOn updates through the current
	/// proof-of-concept version-aware route. Mixed starts, Instruments and
	/// recursive Pattern/Sequence sources require more preparation semantics
	/// and retain the existing session-refresh behavior.</summary>
	private static bool TryGetDirectSynthStarts(
		SongDocument document,
		IReadOnlyList<NoteCommand> commands,
		out IReadOnlyList<ObjectId> sourceIds)
	{
		List<ObjectId> found = [];
		foreach (NoteCommand command in commands)
		{
			if (command is not StartNoteCommand start)
				continue;
			if (start.Mixdown
				|| !document.TryGet(start.SourceId, out SongObject? source)
				|| source is not FmSynthDefinition)
			{
				sourceIds = [];
				return false;
			}
			if (!found.Contains(start.SourceId))
				found.Add(start.SourceId);
		}
		sourceIds = found;
		return found.Count > 0;
	}

	/// <summary>Publish the revision frozen by the successful request,
	/// not the author's potentially changed revision after compilation.</summary>
	private void PublishPlaybackSnapshot(
		SongDocument? document,
		uint audioRevision)
	{
		PlaybackSnapshotInfo state;
		lock (_positionGate)
		{
			state = new PlaybackSnapshotInfo(
				_currentPlaybackSnapshot.Generation + 1, document, audioRevision);
			_currentPlaybackSnapshot = state;
		}

		if (PlaybackSnapshotChanged is not { } observers)
			return;
		PlaybackSnapshotChangedEventArgs args = new(state);
		foreach (EventHandler<PlaybackSnapshotChangedEventArgs> observer
			in observers.GetInvocationList())
		{
			try { observer(this, args); }
			catch (Exception)
			{
				// UI subscribers must never interrupt playback.
			}
		}
	}

	private void StartPositionTracking(
		PlaybackPositionTimeline timeline)
	{
		ArgumentNullException.ThrowIfNull(timeline);

		lock (_positionGate)
		{
			_positionTimeline = timeline;
			_positionStopwatch.Restart();
			SetCurrentPlaybackPositionLocked(
				timeline.GetPositionAt(TimeSpan.Zero));
			_positionTimer.Change(
				dueTime: 5,
				period: 5);
		}
	}

	private void StopPositionTracking(bool keepAudioMonitoring = false)
	{
		lock (_positionGate)
		{
			if (!keepAudioMonitoring)
				_positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
			_positionStopwatch.Reset();
			_positionTimeline = null;
			if (!keepAudioMonitoring)
				_diagnosticRequest = null;
			SetCurrentPlaybackPositionLocked(null);
		}
	}

	private void PollRuntimeDiagnostics()
	{
		PlaybackRequest? request;
		lock (_positionGate)
			request = _diagnosticRequest;
		if (request is null
			|| _sourceFactory is not IPlaybackRuntimeDiagnosticReportProvider provider
			|| !provider.TryTakeRuntimeDiagnostics(request, out var messages)
			|| messages.Length == 0)
			return;
		PlaybackRuntimeDiagnosticsEventArgs args = new(messages);
		if (RuntimeDiagnostics is not { } observers)
			return;
		foreach (EventHandler<PlaybackRuntimeDiagnosticsEventArgs> observer
			in observers.GetInvocationList())
		{
			try { observer(this, args); }
			catch (Exception)
			{
				// Faulty UI/logging subscribers are never allowed to stop audio.
			}
		}
	}

	/// <summary>Sample session health without executing code in SDL's callback.</summary>
	private void PollAudioHealth()
	{
		PlaybackAudioHealthSnapshot health = _controller.GetAudioHealth();
		PlaybackAudioHealthChangedEventArgs? args;
		lock (_positionGate)
		{
			if (health.SessionId < _lastAudioSessionId)
				return;
			bool changed = health.SessionId != _lastAudioSessionId;
			if (changed)
			{
				_lastAudioSessionId = health.SessionId;
				_lastAudioUnderruns = 0;
				_lastAudioFaultReported = false;
			}
			bool newFault = health.Fault is not null && !_lastAudioFaultReported;
			if (!changed && !newFault
				&& health.UnderrunCount == _lastAudioUnderruns)
				return;
			_lastAudioUnderruns = health.UnderrunCount;
			if (newFault)
				_lastAudioFaultReported = true;
			args = new PlaybackAudioHealthChangedEventArgs(health, newFault);
		}
		if (AudioHealthChanged is not { } observers)
			return;
		foreach (EventHandler<PlaybackAudioHealthChangedEventArgs> observer
			in observers.GetInvocationList())
		{
			try { observer(this, args); }
			catch (Exception)
			{
				// UI/logging subscribers must never interrupt transport polling.
			}
		}
	}

	private void PollPlaybackPosition()
	{
		lock (_positionGate)
		{
			PlaybackPositionTimeline? timeline =
				_positionTimeline;
			if (timeline is null)
				return;

			TimeSpan elapsed =
				_positionStopwatch.Elapsed;
			SetCurrentPlaybackPositionLocked(
				timeline.GetPositionAt(elapsed));

			if (timeline.IsComplete(elapsed))
			{
				_positionTimeline = null;
				_positionStopwatch.Stop();
				// Musical position is finished, but an output worker may
				// still render tails, fault or underrun before Stop.
				_positionTimer.Change(dueTime: 100, period: 100);
			}
		}
	}

	private void SetCurrentPlaybackPositionLocked(
		PlaybackPatternPosition? position)
	{
		if (_currentPlaybackPosition == position)
			return;

		_currentPlaybackPosition = position;
		PlaybackPositionChanged?.Invoke(
			this,
			new PlaybackPositionChangedEventArgs(
				position));
	}

	public void Dispose()
	{
		StopPositionTracking();
		_controller.Dispose();
		_positionTimer.Dispose();
		_commandGate.Dispose();
	}
}
