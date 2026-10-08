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
		IPlaybackRuntimeDiagnosticsTransport
{
	private readonly BackgroundPlaybackController _controller;
	private readonly IBackgroundPlaybackSourceFactory _sourceFactory;
	private readonly SemaphoreSlim _commandGate = new(1, 1);
	private readonly object _positionGate = new();
	private readonly Stopwatch _positionStopwatch = new();
	private readonly Timer _positionTimer;

	private PlaybackPositionTimeline? _positionTimeline;
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
				_ => PollPlaybackPosition(),
				null,
				Timeout.Infinite,
				Timeout.Infinite);
	}

	public event EventHandler<PlaybackPositionChangedEventArgs>?
		PlaybackPositionChanged;
	public event EventHandler<PlaybackRuntimeDiagnosticsEventArgs>?
		RuntimeDiagnostics;

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
				startPosition));

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
				repeat));

	public Task PlayAdHocAsync(
		SongDocument document,
		NoteSchedule schedule)
		=> PlayRequestAsync(
			AdHocPlaybackRequest.Create(
				document,
				schedule));

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
			StopPositionTracking();
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
		}
		finally
		{
			_commandGate.Release();
		}
	}

	private async Task PlayRequestAsync(
		PlaybackRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			StopPositionTracking();
			await _controller.PlayAsync(request).ConfigureAwait(false);

			if (_sourceFactory is IPlaybackRuntimeDiagnosticReportProvider diagnostics
				&& diagnostics.TryTakeRuntimeDiagnostics(
					request, out var messages)
				&& messages.Length != 0)
			{
				// Playback compilation is complete. No UI callback is
				// invoked from the audio thread. Consumers marshal as needed.
				RuntimeDiagnostics?.Invoke(
					this, new PlaybackRuntimeDiagnosticsEventArgs(messages));
			}

			if (_sourceFactory is IPlaybackPositionTimelineProvider provider
				&& provider.TryTakePlaybackPositionTimeline(
					request,
					out PlaybackPositionTimeline? timeline)
				&& timeline is not null)
			{
				StartPositionTracking(timeline);
			}
		}
		catch
		{
			StopPositionTracking();
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
			uint revision = document.AudioRevision;
			await _controller.PlayAsync(
					AdHocPlaybackRequest.Create(
						document,
						new NoteScheduleBuilder().Freeze()))
				.ConfigureAwait(false);
			_liveAuditionActive = true;
			_liveAuditionDocument = document;
			_liveAuditionAudioRevision = revision;
		}
		catch
		{
			_liveAuditionActive = false;
			_liveAuditionDocument = null;
			throw;
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

	private void StopPositionTracking()
	{
		lock (_positionGate)
		{
			_positionTimer.Change(
				Timeout.Infinite,
				Timeout.Infinite);
			_positionStopwatch.Reset();
			_positionTimeline = null;
			SetCurrentPlaybackPositionLocked(null);
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
				_positionTimer.Change(
					Timeout.Infinite,
					Timeout.Infinite);
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
