using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackPositionTimelineTests
{
	[Test]
	public void FiniteTimelineSelectsCurrentRowAndClearsAtLogicalEnd()
	{
		ObjectId patternId = (ObjectId)7U;
		PlaybackPositionTimeline timeline =
			new(
				[
					new PlaybackPositionTimelineEntry(
						TimeSpan.Zero,
						new PlaybackPatternPosition(
							patternId,
							0,
							null,
							null)),
					new PlaybackPositionTimelineEntry(
						TimeSpan.FromMilliseconds(120),
						new PlaybackPatternPosition(
							patternId,
							1,
							null,
							null)),
				],
				TimeSpan.FromMilliseconds(240),
				repeat: false);

		timeline.GetPositionAt(TimeSpan.Zero)!.PatternRow
			.Should().Be(0);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(119))!.PatternRow
			.Should().Be(0);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(120))!.PatternRow
			.Should().Be(1);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(239))!.PatternRow
			.Should().Be(1);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(240))
			.Should().BeNull();
	}

	[Test]
	public void RepeatingTimelineWrapsAtCycleDuration()
	{
		ObjectId patternId = (ObjectId)7U;
		PlaybackPositionTimeline timeline =
			new(
				[
					new PlaybackPositionTimelineEntry(
						TimeSpan.Zero,
						new PlaybackPatternPosition(
							patternId,
							0,
							null,
							null)),
					new PlaybackPositionTimelineEntry(
						TimeSpan.FromMilliseconds(120),
						new PlaybackPatternPosition(
							patternId,
							1,
							null,
							null)),
				],
				TimeSpan.FromMilliseconds(240),
				repeat: true);

		timeline.GetPositionAt(TimeSpan.FromMilliseconds(250))!.PatternRow
			.Should().Be(0);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(370))!.PatternRow
			.Should().Be(1);
	}

	[Test]
	public async Task TransportPublishesInitialPositionAndClearsImmediatelyOnStop()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new DataPatternDefinition(
				patternId,
				"Pattern"));

		TimelineFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(
				backend,
				factory);
		List<PlaybackPatternPosition?> positions = [];
		transport.PlaybackPositionChanged +=
			(_, e) => positions.Add(e.Position);

		await transport.PlayPatternAsync(
			document,
			patternId,
			repeat: true);

		transport.CurrentPlaybackPosition.Should().Be(
			new PlaybackPatternPosition(
				patternId,
				0,
				null,
				null));
		positions.Should().ContainSingle()
			.Which.Should().Be(
				transport.CurrentPlaybackPosition);

		await transport.StopAsync();

		transport.CurrentPlaybackPosition.Should().BeNull();
		positions.Should().HaveCount(2);
		positions[^1].Should().BeNull();
	}

	private sealed class TimelineFactory
		: IBackgroundPlaybackSourceFactory,
			IPlaybackPositionTimelineProvider
	{
		private PlaybackRequest? _request;
		private PlaybackPositionTimeline? _timeline;

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			_request = request;
			if (request is PatternPlaybackRequest pattern)
			{
				_timeline =
					new PlaybackPositionTimeline(
						[
							new PlaybackPositionTimelineEntry(
								TimeSpan.Zero,
								new PlaybackPatternPosition(
									pattern.PatternId,
									0,
									null,
									null)),
						],
						TimeSpan.FromSeconds(10),
						pattern.Repeat);
			}
			return new SilentSource();
		}

		public bool TryTakePlaybackPositionTimeline(
			PlaybackRequest request,
			out PlaybackPositionTimeline? timeline)
		{
			if (!ReferenceEquals(
				request,
				_request))
			{
				timeline = null;
				return false;
			}

			timeline = _timeline;
			_request = null;
			_timeline = null;
			return timeline is not null;
		}
	}

	private sealed class SilentSource : IAudioOutputSource
	{
		public AudioOutputFormat Format =>
			new(48000, 2);

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
