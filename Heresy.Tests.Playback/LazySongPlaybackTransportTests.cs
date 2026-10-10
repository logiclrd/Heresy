using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Diagnostics;
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

	[Test]
	public async Task DiagnosticsSubscribedBeforeLazyInitializationAreForwarded()
	{
		ProbeTransport? probe = null;
		int created = 0;
		using LazySongPlaybackTransport lazy = new(() =>
		{
			created++;
			probe = new ProbeTransport();
			return probe;
		});
		List<PlaybackRuntimeDiagnosticsEventArgs> received = [];
		EventHandler<PlaybackRuntimeDiagnosticsEventArgs> handler =
			(_, e) => received.Add(e);
		((IPlaybackRuntimeDiagnosticsTransport)lazy).RuntimeDiagnostics += handler;
		created.Should().Be(0);

		await lazy.PlayPatternAsync(new SongDocument(), (ObjectId)1U);
		probe!.EmitDiagnostic();
		received.Should().ContainSingle();
		received[0].Diagnostics[0].Code.Should().Be("HRSEQ001");

		((IPlaybackRuntimeDiagnosticsTransport)lazy).RuntimeDiagnostics -= handler;
		probe.EmitDiagnostic();
		received.Should().ContainSingle();
	}

	[Test]
	public async Task HealthSubscriberSurvivesLazyCreationAndCanBeRemoved()
	{
		int created = 0;
		ProbeTransport? probe = null;
		using LazySongPlaybackTransport lazy = new(() =>
		{
			created++;
			probe = new ProbeTransport();
			return probe;
		});
		List<PlaybackAudioHealthChangedEventArgs> received = [];
		EventHandler<PlaybackAudioHealthChangedEventArgs> handler =
			(_, e) => received.Add(e);
		((IPlaybackAudioHealthTransport)lazy).AudioHealthChanged += handler;
		created.Should().Be(0);
		await lazy.PlayPatternAsync(new SongDocument(), (ObjectId)1U);
		probe!.EmitHealth();
		received.Should().ContainSingle();
		received[0].UnderrunCount.Should().Be(2);

		((IPlaybackAudioHealthTransport)lazy).AudioHealthChanged -= handler;
		probe.EmitHealth();
		received.Should().ContainSingle();
	}

	[Test]
	public async Task SnapshotSubscribersSurviveLazyInitializationWithoutEagerAudio()
	{
		ProbeTransport? probe = null;
		int created = 0;
		using LazySongPlaybackTransport lazy = new(() =>
		{
			created++;
			probe = new ProbeTransport();
			return probe;
		});
		List<PlaybackSnapshotInfo> received = [];
		EventHandler<PlaybackSnapshotChangedEventArgs> handler =
			(_, e) => received.Add(e.Snapshot);
		IPlaybackSnapshotTransport snapshot = lazy;
		snapshot.PlaybackSnapshotChanged += handler;
		snapshot.CurrentPlaybackSnapshot.IsActive.Should().BeFalse();
		created.Should().Be(0);
		await lazy.PlayPatternAsync(new SongDocument(), (ObjectId)1U);
		probe!.EmitSnapshot();
		received.Should().ContainSingle();
		received[0].IsActive.Should().BeTrue();
		snapshot.CurrentPlaybackSnapshot.IsActive.Should().BeTrue();
		snapshot.PlaybackSnapshotChanged -= handler;
		probe.EmitSnapshot();
		received.Should().ContainSingle();
	}

	private sealed class ProbeTransport
		: ISongPlaybackTransport, IPlaybackRuntimeDiagnosticsTransport,
			IPlaybackAudioHealthTransport, IPlaybackSnapshotTransport
	{
		public event EventHandler<PlaybackRuntimeDiagnosticsEventArgs>?
			RuntimeDiagnostics;
		public event EventHandler<PlaybackAudioHealthChangedEventArgs>?
			AudioHealthChanged;
		public event EventHandler<PlaybackSnapshotChangedEventArgs>?
			PlaybackSnapshotChanged;
		public PlaybackSnapshotInfo CurrentPlaybackSnapshot { get; private set; }
		public void EmitSnapshot()
		{
			CurrentPlaybackSnapshot = new PlaybackSnapshotInfo(
				CurrentPlaybackSnapshot.Generation + 1,
				new SongDocument(), 0);
			PlaybackSnapshotChanged?.Invoke(this,
				new PlaybackSnapshotChangedEventArgs(CurrentPlaybackSnapshot));
		}
		public void EmitHealth()
			=> AudioHealthChanged?.Invoke(this,
				new PlaybackAudioHealthChangedEventArgs(
					new PlaybackAudioHealthSnapshot(7, true, 2, null),
					isNewFault: false));

		public void EmitDiagnostic()
			=> RuntimeDiagnostics?.Invoke(this,
				new PlaybackRuntimeDiagnosticsEventArgs(
				[
					new SequencingDiagnostic(
						"HRSEQ001", "Dropped out-of-order Pattern note", 1, 3),
				]));

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

		public Task SendLiveEventAsync(
			SongDocument document,
			ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
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
