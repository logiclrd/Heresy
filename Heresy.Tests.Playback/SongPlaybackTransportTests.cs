using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

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
		public IAudioOutputSession Open(
			AudioOutputFormat format,
			IAudioOutputSource source)
			=> new TestSession(format);

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
