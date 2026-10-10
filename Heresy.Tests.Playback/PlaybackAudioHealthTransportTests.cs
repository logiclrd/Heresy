using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackAudioHealthTransportTests
{
	[Test]
	public async Task UnderrunsAndWorkerFaultAreReportedOnceAndResetForNextSession()
	{
		HealthBackend backend = new();
		using SongPlaybackTransport transport = new(backend, new SilentFactory());
		ConcurrentQueue<PlaybackAudioHealthChangedEventArgs> reports = new();
		((IPlaybackAudioHealthTransport)transport).AudioHealthChanged +=
			(_, e) => reports.Enqueue(e);
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Health"));

		await transport.PlayPatternAsync(document, patternId);
		Assert.That(reports.ToArray().Any(e => e.IsActive
			&& e.UnderrunCount == 0 && !e.IsNewFault), Is.True);
		HealthSession first = backend.Opened[0];
		first.SetUnderruns(3);
		first.SetFault(new InvalidOperationException("PCM source failed"));
		Assert.That(SpinWait.SpinUntil(
			() => reports.ToArray().Any(e => e.IsActive
				&& e.UnderrunCount == 3 && e.IsNewFault
				&& e.Fault?.Message == "PCM source failed"),
			TimeSpan.FromSeconds(5)), Is.True);
		Thread.Sleep(120);
		Assert.That(reports.ToArray().Count(e => e.IsNewFault), Is.EqualTo(1),
			"The transport must not flood the diagnostics window with the same fault.");

		await transport.StopAsync();
		Assert.That(reports.ToArray().Last().IsActive, Is.False);
		await transport.PlayPatternAsync(document, patternId);
		PlaybackAudioHealthChangedEventArgs reset = reports.ToArray().Last();
		Assert.That(reset.IsActive, Is.True);
		Assert.That(reset.UnderrunCount, Is.Zero);
		Assert.That(reset.Fault, Is.Null);
		Assert.That(reset.SessionId, Is.GreaterThan(
			reports.ToArray().First().SessionId));
	}

	[Test]
	public async Task FaultingHealthSubscriberDoesNotInterruptOtherObserversOrPlayback()
	{
		HealthBackend backend = new();
		using SongPlaybackTransport transport = new(backend, new SilentFactory());
		int received = 0;
		((IPlaybackAudioHealthTransport)transport).AudioHealthChanged +=
			(_, _) => throw new InvalidOperationException("Observer failed");
		((IPlaybackAudioHealthTransport)transport).AudioHealthChanged +=
			(_, _) => Interlocked.Increment(ref received);
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Health"));

		Assert.DoesNotThrowAsync(async () =>
			await transport.PlayPatternAsync(document, patternId));
		Assert.That(Volatile.Read(ref received), Is.GreaterThan(0));
	}

	private sealed class SilentFactory : IBackgroundPlaybackSourceFactory
	{
		public IAudioOutputSource Create(PlaybackRequest request)
			=> new SilentSource();
	}

	private sealed class SilentSource : IAudioOutputSource
	{
		public AudioOutputFormat Format => new(48000, 2);
		public void Render(int frameCount, Span<float> destination)
			=> destination.Clear();
	}

	private sealed class HealthBackend : IAudioOutputBackend
	{
		public List<HealthSession> Opened { get; } = [];
		public IAudioOutputSession Open(
			AudioOutputFormat format, IAudioOutputSource source)
		{
			HealthSession session = new(format);
			Opened.Add(session);
			return session;
		}
		public void Dispose() { }
	}

	private sealed class HealthSession :
		IAudioOutputSession, IAudioOutputUnderrunCounter
	{
		private long _underruns;
		private Exception? _fault;
		public HealthSession(AudioOutputFormat format) => Format = format;
		public AudioOutputFormat Format { get; }
		public bool IsRunning { get; private set; }
		public long UnderrunCount => Interlocked.Read(ref _underruns);
		public Exception? Fault => Volatile.Read(ref _fault);
		public void SetUnderruns(long count) => Interlocked.Exchange(ref _underruns, count);
		public void SetFault(Exception error) => Volatile.Write(ref _fault, error);
		public void Start() => IsRunning = true;
		public void Stop() => IsRunning = false;
		public void Dispose() { }
	}
}
