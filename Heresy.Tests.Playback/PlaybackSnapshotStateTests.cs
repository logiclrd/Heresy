using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackSnapshotStateTests
{
	[Test]
	public async Task PlaybackCapturesActualRequestRevisionAndOnlyAudioEditsMakeItStale()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Pattern"));
		Backend backend = new();
		using SongPlaybackTransport transport = new(backend, new Factory());
		List<PlaybackSnapshotInfo> changes = [];
		((IPlaybackSnapshotTransport)transport).PlaybackSnapshotChanged +=
			(_, e) => changes.Add(e.Snapshot);

		await transport.PlayPatternAsync(document, patternId);
		PlaybackSnapshotInfo initial =
			((IPlaybackSnapshotTransport)transport).CurrentPlaybackSnapshot;
		Assert.Multiple(() =>
		{
			Assert.That(initial.IsActive, Is.True);
			Assert.That(initial.SourceDocument, Is.SameAs(document));
			Assert.That(initial.AudioRevision, Is.EqualTo(document.AudioRevision));
			Assert.That(PlaybackSnapshotIndicator.GetState(initial, document),
				Is.EqualTo(PlaybackSnapshotIndicatorState.Current));
		});

		document.MarkChanged(affectsAudio: false);
		Assert.That(PlaybackSnapshotIndicator.GetState(initial, document),
			Is.EqualTo(PlaybackSnapshotIndicatorState.Current));
		document.MarkChanged(affectsAudio: true);
		Assert.That(PlaybackSnapshotIndicator.GetState(initial, document),
			Is.EqualTo(PlaybackSnapshotIndicatorState.AudioEdited));
		Assert.That(PlaybackSnapshotIndicator.GetState(initial, new SongDocument()),
			Is.EqualTo(PlaybackSnapshotIndicatorState.DifferentDocument));

		await transport.PlayPatternAsync(document, patternId);
		PlaybackSnapshotInfo refreshed =
			((IPlaybackSnapshotTransport)transport).CurrentPlaybackSnapshot;
		Assert.That(refreshed.Generation, Is.GreaterThan(initial.Generation));
		Assert.That(refreshed.AudioRevision, Is.EqualTo(document.AudioRevision));
		Assert.That(PlaybackSnapshotIndicator.GetState(refreshed, document),
			Is.EqualTo(PlaybackSnapshotIndicatorState.Current));

		await transport.StopAsync();
		PlaybackSnapshotInfo stopped =
			((IPlaybackSnapshotTransport)transport).CurrentPlaybackSnapshot;
		Assert.That(stopped.IsActive, Is.False);
		Assert.That(stopped.Generation, Is.GreaterThan(refreshed.Generation));
		Assert.That(PlaybackSnapshotIndicator.GetState(stopped, document),
			Is.EqualTo(PlaybackSnapshotIndicatorState.Inactive));
		Assert.That(changes.Count, Is.EqualTo(3));
	}

	[Test]
	public async Task LiveReleaseRetainsSnapshotWhileNextNoteAfterAudioEditRefreshes()
	{
		SongDocument document = new();
		using SongPlaybackTransport transport = new(new Backend(), new Factory());
		IPlaybackSnapshotTransport state = transport;
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(1),
			[new StartNoteCommand((ObjectId)123U)]);
		PlaybackSnapshotInfo original = state.CurrentPlaybackSnapshot;
		document.MarkChanged(affectsAudio: true);
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(1),
			[new NoteOffCommand()]);
		Assert.That(state.CurrentPlaybackSnapshot, Is.EqualTo(original));
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(2),
			[new StartNoteCommand((ObjectId)123U)]);
		Assert.That(state.CurrentPlaybackSnapshot.Generation,
			Is.GreaterThan(original.Generation));
		Assert.That(state.CurrentPlaybackSnapshot.AudioRevision,
			Is.EqualTo(document.AudioRevision));
	}

	[Test]
	public async Task FailedReplacementClearsPreviouslyPublishedSnapshot()
	{
		Backend backend = new();
		using SongPlaybackTransport transport = new(backend, new Factory());
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Pattern"));
		await transport.PlayPatternAsync(document, patternId);
		backend.FailNextOpen = true;
		Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await transport.PlayPatternAsync(document, patternId));
		Assert.That(((IPlaybackSnapshotTransport)transport)
			.CurrentPlaybackSnapshot.IsActive, Is.False);
	}

	[Test]
	public void SongDocumentChangeEventIncludesAudioClassification()
	{
		SongDocument document = new();
		List<bool> affectedAudio = [];
		document.Changed += (_, e) => affectedAudio.Add(e.AffectsAudio);
		document.MarkChanged(affectsAudio: false);
		document.MarkChanged(affectsAudio: true);
		Assert.That(affectedAudio, Is.EqualTo(new[] { false, true }));
		Assert.That(document.DocumentRevision, Is.EqualTo(2));
		Assert.That(document.AudioRevision, Is.EqualTo(1));
	}

	private sealed class Factory : IBackgroundPlaybackSourceFactory
	{
		public IAudioOutputSource Create(PlaybackRequest request) => new Source();
	}
	private sealed class Source : ILiveAudioOutputSource
	{
		public AudioOutputFormat Format => new(48000, 2);
		public void Render(int count, Span<float> buffer) => buffer.Clear();
		public void EnqueueLiveEvent(ChannelTarget channel, IReadOnlyList<NoteCommand> commands) { }
	}
	private sealed class Backend : IAudioOutputBackend
	{
		public bool FailNextOpen { get; set; }
		public IAudioOutputSession Open(AudioOutputFormat format, IAudioOutputSource source)
		{
			if (FailNextOpen)
			{
				FailNextOpen = false;
				throw new InvalidOperationException("Device unavailable");
			}
			return new Session(format);
		}
		public void Dispose() { }
	}
	private sealed class Session(AudioOutputFormat format) : IAudioOutputSession
	{
		public AudioOutputFormat Format { get; } = format;
		public bool IsRunning { get; private set; }
		public Exception? Fault => null;
		public void Start() => IsRunning = true;
		public void Stop() => IsRunning = false;
		public void Dispose() { }
	}
}
