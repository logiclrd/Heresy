using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class SongPlaybackTransportTests
{
	[Test]
	public async Task RuntimeDiagnosticsArePublishedFromPreparedSourceAfterPlay()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Test"));

		DiagnosticFactory factory = new();
		using SongPlaybackTransport transport = new(new TestBackend(), factory);
		List<PlaybackRuntimeDiagnosticsEventArgs> events = [];
		((IPlaybackRuntimeDiagnosticsTransport)transport).RuntimeDiagnostics +=
			(_, e) => events.Add(e);

		await transport.PlayPatternAsync(document, id);
		events.Should().ContainSingle();
		events[0].Diagnostics.Should().ContainSingle();
		events[0].Diagnostics[0].Code.Should().Be("HRSEQ001");
		events[0].Diagnostics[0].Row.Should().Be(2);

		await transport.PlayPatternAsync(document, id);
		events.Should().HaveCount(2);
		events[1].Diagnostics.Should().ContainSingle();
		events[1].Diagnostics[0].Row.Should().Be(2);
	}

	private sealed class DiagnosticFactory :
		IBackgroundPlaybackSourceFactory,
		IPlaybackRuntimeDiagnosticReportProvider
	{
		private readonly Dictionary<PlaybackRequest, SequencingDiagnostic[]>
			_pending = new(ReferenceEqualityComparer.Instance);

		public IAudioOutputSource Create(PlaybackRequest request)
		{
			_pending[request] = [
				new SequencingDiagnostic("HRSEQ001",
					"Dropped out-of-order Pattern note at row 2", 2, 4),
			];
			return new SilentSource();
		}

		public bool TryTakeRuntimeDiagnostics(
			PlaybackRequest request,
			out SequencingDiagnostic[] diagnostics)
		{
			if (_pending.Remove(request, out SequencingDiagnostic[]? found))
			{
				diagnostics = found;
				return true;
			}
			diagnostics = [];
			return false;
		}
	}

	[Test]
	public async Task PlaySongTargetsCurrentRootSequence()
	{
		SongDocument document = new();
		ObjectId rootId = document.AllocateObjectId();
		document.Add(
			new Heresy.Core.Sequences.DataSequenceDefinition(
				rootId,
				"Root"));
		document.RootSequenceId = rootId;
		RecordingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.PlaySongAsync(document);

		factory.Requests.Should().ContainSingle()
			.Which.Should()
			.BeOfType<SequencePlaybackRequest>()
			.Which.SequenceId.Should().Be(rootId);
	}

	[Test]
	public async Task PatternRequestCarriesStartAndRepeat()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new DataPatternDefinition(
				patternId,
				"Pattern"));
		RecordingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.PlayPatternAsync(
			document,
			patternId,
			startRow: 12,
			repeat: true);

		PatternPlaybackRequest request =
			factory.Requests.Should().ContainSingle()
				.Which.Should()
				.BeOfType<PatternPlaybackRequest>()
				.Which;
		request.PatternId.Should().Be(patternId);
		request.StartRow.Should().Be(12);
		request.Repeat.Should().BeTrue();
	}

	[Test]
	public async Task LiveAuditionStartsOneSessionAndQueuesStartThenNoteOff()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new NoteOffCommand()]);

		factory.Source.Events.Should().HaveCount(2);
		factory.Source.Events[0].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[0].Commands.Should().ContainSingle()
			.Which.Should().Be(
				new StartNoteCommand((ObjectId)17U));
		factory.Source.Events[1].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[1].Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteOffCommand>();
		backend.OpenCount.Should().Be(1);
	}


	[Test]
	public async Task LiveEventsEnsureOneSessionAndPreserveRequestedTargets()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)18U)]);

		backend.OpenCount.Should().Be(1);
		factory.Source.Events.Should().HaveCount(2);
		factory.Source.Events[0].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[1].Target.Should().Be(
			ChannelTarget.Virtual(42));
	}

	[Test]
	public async Task LiveEventAfterStopStartsFreshSession()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.StopAsync();
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)17U)]);

		backend.OpenCount.Should().Be(2);
	}


	[Test]
	public async Task NewLiveNoteAfterAudioEditRefreshesPlaybackSnapshot()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		document.MarkChanged(affectsAudio: true);

		// Releasing the running note must not restart its old session.
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new NoteOffCommand()]);
		backend.OpenCount.Should().Be(1);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(2);
	}

	[Test]
	public async Task LiveAuditionReusesSnapshotAcrossLayoutChangesOnly()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		document.MarkChanged(affectsAudio: false);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(43),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task LiveAuditionOnDifferentDocumentUsesFreshSnapshot()
	{
		SongDocument first = new();
		SongDocument second = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			first,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			second,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(2);
	}


	private sealed class LiveFactory
		: IBackgroundPlaybackSourceFactory
	{
		public LiveSource Source { get; } = new();

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			request.Should().BeOfType<AdHocPlaybackRequest>();
			return Source;
		}
	}

	private sealed class LiveSource : ILiveAudioOutputSource
	{
		public List<LivePlaybackEvent> Events { get; } = [];

		public AudioOutputFormat Format => new(48000, 2);

		public void EnqueueLiveEvent(
			ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
			=> Events.Add(
				new LivePlaybackEvent(
					target,
					commands));

		public void Render(
			int frameCount,
			Span<float> destination)
			=> destination.Clear();
	}

	private sealed class RecordingFactory
		: IBackgroundPlaybackSourceFactory
	{
		public List<PlaybackRequest> Requests { get; } = [];

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			Requests.Add(request);
			return new SilentSource();
		}
	}

	private sealed class SilentSource : IAudioOutputSource
	{
		public AudioOutputFormat Format => new(48000, 2);

		public void Render(
			int frameCount,
			Span<float> destination)
			=> destination.Clear();
	}

	private sealed class TestBackend : IAudioOutputBackend
	{
		public int OpenCount { get; private set; }

		public IAudioOutputSession Open(
			AudioOutputFormat format,
			IAudioOutputSource source)
		{
			OpenCount++;
			return new TestSession(format);
		}

		public void Dispose()
		{
		}
	}

	private sealed class TestSession : IAudioOutputSession
	{
		public TestSession(
			AudioOutputFormat format)
			=> Format = format;

		public AudioOutputFormat Format { get; }
		public bool IsRunning { get; private set; }
		public Exception? Fault => null;

		public void Start()
			=> IsRunning = true;

		public void Stop()
			=> IsRunning = false;

		public void Dispose()
		{
		}
	}
}
