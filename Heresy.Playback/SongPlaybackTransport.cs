using System;
using System.Collections.Generic;
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

	Task BeginLiveAuditionAsync(
		SongDocument document);

	Task SendLiveEventAsync(
		SongDocument document,
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands);

	Task StartLiveNoteAsync(
		int voiceId,
		StartNoteCommand command);

	Task ReleaseLiveNoteAsync(
		int voiceId);

	Task StopAsync();
}

public sealed class SongPlaybackTransport
	: ISongPlaybackTransport
{
	private readonly BackgroundPlaybackController _controller;
	private readonly SemaphoreSlim _commandGate = new(1, 1);
	private bool _liveAuditionActive;

	public SongPlaybackTransport(
		IAudioOutputBackend backend,
		IBackgroundPlaybackSourceFactory sourceFactory)
	{
		_controller =
			new BackgroundPlaybackController(
				backend
					?? throw new ArgumentNullException(nameof(backend)),
				sourceFactory
					?? throw new ArgumentNullException(nameof(sourceFactory)));
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

	public async Task BeginLiveAuditionAsync(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await EnsureLiveAuditionCoreAsync(document)
				.ConfigureAwait(false);
		}
		finally
		{
			_commandGate.Release();
		}
	}

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
			await EnsureLiveAuditionCoreAsync(document)
				.ConfigureAwait(false);
			await _controller.SendLiveEventAsync(
					target,
					commands)
				.ConfigureAwait(false);
		}
		catch
		{
			_liveAuditionActive = false;
			throw;
		}
		finally
		{
			_commandGate.Release();
		}
	}

	public Task StartLiveNoteAsync(
		int voiceId,
		StartNoteCommand command)
	{
		if (voiceId < 0)
			throw new ArgumentOutOfRangeException(nameof(voiceId));
		ArgumentNullException.ThrowIfNull(command);

		return _controller.SendLiveEventAsync(
			ChannelTarget.Physical(voiceId),
			[command]);
	}

	public Task ReleaseLiveNoteAsync(
		int voiceId)
	{
		if (voiceId < 0)
			throw new ArgumentOutOfRangeException(nameof(voiceId));

		return _controller.SendLiveEventAsync(
			ChannelTarget.Physical(voiceId),
			[new NoteOffCommand()]);
	}

	public async Task StopAsync()
	{
		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_liveAuditionActive = false;
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
			await _controller.PlayAsync(request).ConfigureAwait(false);
		}
		finally
		{
			_commandGate.Release();
		}
	}

	private async Task EnsureLiveAuditionCoreAsync(
		SongDocument document)
	{
		if (_liveAuditionActive)
			return;

		try
		{
			await _controller.PlayAsync(
					AdHocPlaybackRequest.Create(
						document,
						new NoteScheduleBuilder().Freeze()))
				.ConfigureAwait(false);
			_liveAuditionActive = true;
		}
		catch
		{
			_liveAuditionActive = false;
			throw;
		}
	}

	public void Dispose()
	{
		_controller.Dispose();
		_commandGate.Dispose();
	}
}
