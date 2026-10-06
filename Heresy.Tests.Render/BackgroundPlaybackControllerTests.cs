using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class BackgroundPlaybackControllerTests
{
	[Test]
	public async Task SequenceRequestCapturesSongBeforeWorkerProcessesIt()
	{
		SongDocument document = new();
		ObjectId sequenceId = (ObjectId)17U;
		BlockingFactory factory = new();
		TestBackend backend = new();
		using BackgroundPlaybackController controller =
			new(backend, factory);

		Task play =
			controller.PlayAsync(
				SequencePlaybackRequest.Create(
					document,
					sequenceId,
					new SequencePlaybackPosition(2, 7)));

		factory.WaitUntilEntered();
		document.Root.Name = "Edited after submit";
		factory.Release();
		await play;

		SequencePlaybackRequest request =
			factory.Requests.Should()
				.ContainSingle()
				.Subject.Should()
				.BeOfType<SequencePlaybackRequest>()
				.Subject;
		request.Snapshot.Document.Root.Name.Should().Be("Song");
		request.SequenceId.Should().Be(sequenceId);
		request.StartPosition.Should().Be(
			new SequencePlaybackPosition(2, 7));
	}

	[Test]
	public async Task WorkerCreatesPlaybackSourceOnDedicatedThread()
	{
		SongDocument document = new();
		RecordingFactory factory = new();
		TestBackend backend = new();
		using BackgroundPlaybackController controller =
			new(backend, factory);
		int callerThread = Environment.CurrentManagedThreadId;

		await controller.PlayAsync(
			PatternPlaybackRequest.Create(
				document,
				(ObjectId)5U,
				startRow: 3,
				repeat: true));

		factory.ThreadIds.Should().ContainSingle();
		factory.ThreadIds[0].Should().NotBe(callerThread);
		backend.Opened.Should().ContainSingle();
		backend.Opened[0].Started.Should().BeTrue();
	}

	[Test]
	public async Task NewPlayStopsAndDisposesPreviousSession()
	{
		RecordingFactory factory = new();
		TestBackend backend = new();
		using BackgroundPlaybackController controller =
			new(backend, factory);
		SongDocument document = new();

		await controller.PlayAsync(
			SequencePlaybackRequest.Create(
				document,
				(ObjectId)1U));
		TestSession first = backend.Opened.Should().ContainSingle().Subject;

		await controller.PlayAsync(
			SequencePlaybackRequest.Create(
				document,
				(ObjectId)2U));

		first.Stopped.Should().BeTrue();
		first.Disposed.Should().BeTrue();
		backend.Opened.Should().HaveCount(2);
		backend.Opened[1].Started.Should().BeTrue();
	}

	[Test]
	public async Task StopIsSafeWhenNothingIsPlayingAndStopsCurrentPlayback()
	{
		RecordingFactory factory = new();
		TestBackend backend = new();
		using BackgroundPlaybackController controller =
			new(backend, factory);
		SongDocument document = new();

		await controller.StopAsync();
		await controller.PlayAsync(
			SequencePlaybackRequest.Create(
				document,
				(ObjectId)1U));
		TestSession session = backend.Opened.Should().ContainSingle().Subject;

		await controller.StopAsync();
		await controller.StopAsync();

		session.Stopped.Should().BeTrue();
		session.Disposed.Should().BeTrue();
	}

	[Test]
	public async Task AdHocRequestCarriesImmutableScheduleAndSongSnapshot()
	{
		SongDocument document = new();
		NoteSchedule schedule =
			new NoteScheduleBuilder().Freeze();
		RecordingFactory factory = new();
		TestBackend backend = new();
		using BackgroundPlaybackController controller =
			new(backend, factory);

		await controller.PlayAsync(
			AdHocPlaybackRequest.Create(
				document,
				schedule));

		AdHocPlaybackRequest request =
			factory.Requests.Should()
				.ContainSingle()
				.Subject.Should()
				.BeOfType<AdHocPlaybackRequest>()
				.Subject;
		request.Schedule.Should().BeSameAs(schedule);
		request.Snapshot.Document.Should().NotBeSameAs(document);
	}

	private sealed class RecordingFactory
		: IBackgroundPlaybackSourceFactory
	{
		public List<PlaybackRequest> Requests { get; } = [];
		public List<int> ThreadIds { get; } = [];

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			Requests.Add(request);
			ThreadIds.Add(Environment.CurrentManagedThreadId);
			return new SilentSource();
		}
	}

	private sealed class BlockingFactory
		: IBackgroundPlaybackSourceFactory
	{
		private readonly ManualResetEventSlim _entered = new();
		private readonly ManualResetEventSlim _release = new();

		public List<PlaybackRequest> Requests { get; } = [];

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			Requests.Add(request);
			_entered.Set();
			_release.Wait();
			return new SilentSource();
		}

		public void WaitUntilEntered()
			=> _entered.Wait();

		public void Release()
			=> _release.Set();
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
		public List<TestSession> Opened { get; } = [];

		public IAudioOutputSession Open(
			AudioOutputFormat format,
			IAudioOutputSource source)
		{
			TestSession session = new(format);
			Opened.Add(session);
			return session;
		}

		public void Dispose()
		{
		}
	}

	private sealed class TestSession : IAudioOutputSession
	{
		public TestSession(AudioOutputFormat format)
			=> Format = format;

		public AudioOutputFormat Format { get; }
		public bool IsRunning => Started && !Stopped;
		public Exception? Fault => null;
		public bool Started { get; private set; }
		public bool Stopped { get; private set; }
		public bool Disposed { get; private set; }

		public void Start()
			=> Started = true;

		public void Stop()
			=> Stopped = true;

		public void Dispose()
			=> Disposed = true;
	}
}
