using System;
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
		=> _controller.PlayAsync(
			SequencePlaybackRequest.Create(
				document,
				sequenceId,
				startPosition));

	public Task PlayPatternAsync(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		bool repeat = false)
		=> _controller.PlayAsync(
			PatternPlaybackRequest.Create(
				document,
				patternId,
				startRow,
				repeat));

	public Task PlayAdHocAsync(
		SongDocument document,
		NoteSchedule schedule)
		=> _controller.PlayAsync(
			AdHocPlaybackRequest.Create(
				document,
				schedule));

	public Task BeginLiveAuditionAsync(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		return _controller.PlayAsync(
			AdHocPlaybackRequest.Create(
				document,
				new NoteScheduleBuilder().Freeze()));
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

	public Task StopAsync()
		=> _controller.StopAsync();

	public void Dispose()
		=> _controller.Dispose();
}
