using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class LazySongPlaybackTransportTests
{
	[Test]
	public async Task StopBeforePlaybackDoesNotCreateInnerTransport()
	{
		int created = 0;
		using LazySongPlaybackTransport transport =
			new(
				() =>
				{
					created++;
					return new ProbeTransport();
				});

		await transport.StopAsync();

		created.Should().Be(0);
	}

	[Test]
	public async Task FirstPlayCreatesAndReusesInnerTransport()
	{
		int created = 0;
		ProbeTransport? probe = null;
		using LazySongPlaybackTransport transport =
			new(
				() =>
				{
					created++;
					probe = new ProbeTransport();
					return probe;
				});
		SongDocument document = new();
		ObjectId patternId = (ObjectId)1U;

		await transport.PlayPatternAsync(
			document,
			patternId,
			repeat: true);
		await transport.StopAsync();

		created.Should().Be(1);
		probe!.PatternCalls.Should().Be(1);
		probe.StopCalls.Should().Be(1);
	}

	private sealed class ProbeTransport
		: ISongPlaybackTransport
	{
		public int PatternCalls { get; private set; }
		public int StopCalls { get; private set; }

		public Task PlaySongAsync(
			SongDocument document)
			=> Task.CompletedTask;

		public Task PlaySequenceAsync(
			SongDocument document,
			ObjectId sequenceId,
			SequencePlaybackPosition? startPosition = null)
			=> Task.CompletedTask;

		public Task PlayPatternAsync(
			SongDocument document,
			ObjectId patternId,
			int startRow = 0,
			bool repeat = false)
		{
			PatternCalls++;
			return Task.CompletedTask;
		}

		public Task PlayAdHocAsync(
			SongDocument document,
			NoteSchedule schedule)
			=> Task.CompletedTask;

		public Task BeginLiveAuditionAsync(
			SongDocument document)
			=> Task.CompletedTask;

		public Task SendLiveEventAsync(
			SongDocument document,
			ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
			=> Task.CompletedTask;

		public Task StartLiveNoteAsync(
			int voiceId,
			StartNoteCommand command)
			=> Task.CompletedTask;

		public Task ReleaseLiveNoteAsync(
			int voiceId)
			=> Task.CompletedTask;

		public Task StopAsync()
		{
			StopCalls++;
			return Task.CompletedTask;
		}

		public void Dispose()
		{
		}
	}
}
