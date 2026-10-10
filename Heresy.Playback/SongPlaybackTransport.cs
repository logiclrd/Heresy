using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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
		IPlaybackSnapshotTransport
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
			throw;
		}
		finally
		{
			_commandGate.Release();
		}
	}

	public async Task StopAsync()
	{
		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
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

		try
		{
			PlaybackRequest request = AdHocPlaybackRequest.Create(
				document, new NoteScheduleBuilder().Freeze());
			await _controller.PlayAsync(request).ConfigureAwait(false);
			PublishPlaybackSnapshot(document, request.Snapshot.AudioRevision);
			lock (_positionGate)
				_diagnosticRequest = request;
			_positionTimer.Change(dueTime: 5, period: 50);
			PollAudioHealth();
			_liveAuditionActive = true;
			_liveAuditionDocument = document;
			_liveAuditionAudioRevision = request.Snapshot.AudioRevision;
		}
		catch
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			PublishPlaybackSnapshot(null, 0);
			throw;
		}
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
