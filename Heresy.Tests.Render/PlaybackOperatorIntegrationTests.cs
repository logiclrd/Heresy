using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackOperatorIntegrationTests
{
	[Test]
	public void PersistentChannelOperatorLeavesBaselineUntouchedUntilExpiry()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					new SetOverallChannelVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetOverallChannelVolumeSlideCommand(
						4.0,
						TicksPerRow: 6)),
				Event(
					Frame(12),
					new ClearOverallChannelVolumeSlideCommand())),
			new TestResolver((sourceId, new ConstantSound())));

		session.Render(0, 7, new float[7]);

		PlaybackChannelState channel =
			session.GetChannelState(0);

		Assert.That(channel.ActiveOperatorCount, Is.EqualTo(1));
		Assert.That(channel.OverallVolume, Is.EqualTo(0.5).Within(1e-12));
		Assert.That(
			channel.CurrentVoice!.OverallVolume,
			Is.EqualTo(0.65625).Within(1e-12));

		session.Render(7, 6, new float[6]);

		Assert.That(channel.ActiveOperatorCount, Is.Zero);
		Assert.That(
			channel.OverallVolume,
			Is.EqualTo(0.8125).Within(1e-12));
		Assert.That(
			channel.CurrentVoice!.OverallVolume,
			Is.EqualTo(0.8125).Within(1e-12));
	}

	[Test]
	public void TransientChannelOperatorDisappearsWithoutChangingBaseline()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					new ClearPanbrelloCommand())),
			new TestResolver((sourceId, new ConstantSound())));

		session.Render(0, 3, new float[3]);

		PlaybackChannelState channel =
			session.GetChannelState(0);

		Assert.That(channel.ActiveOperatorCount, Is.EqualTo(1));
		Assert.That(channel.Position, Is.EqualTo(Vector3.Zero));
		Assert.That(
			channel.CurrentVoice!.SoundState.Position.X,
			Is.Not.EqualTo(0.0f).Within(1e-6f));

		session.Render(3, 2, new float[2]);

		Assert.That(channel.ActiveOperatorCount, Is.Zero);
		Assert.That(channel.Position, Is.EqualTo(Vector3.Zero));
		Assert.That(
			channel.CurrentVoice!.SoundState.Position,
			Is.EqualTo(Vector3.Zero));
	}

	[Test]
	public void PersistentSpatialOperatorCommitsOnlyAtExpiry()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					new StartNoteCommand(sourceId),
					new SetSpatialXSlideCommand(
						0.1,
						TicksPerRow: 6)),
				Event(
					Frame(12),
					new ClearSpatialXSlideCommand())),
			new TestResolver((sourceId, new ConstantSound())));

		session.Render(0, 7, new float[7]);

		PlaybackChannelState channel =
			session.GetChannelState(0);
		Assert.That(channel.ActiveOperatorCount, Is.EqualTo(1));
		Assert.That(channel.Position.X, Is.EqualTo(0.0f).Within(1e-6f));
		Assert.That(
			channel.CurrentVoice!.SoundState.Position.X,
			Is.EqualTo(0.25f).Within(1e-6f));

		session.Render(7, 6, new float[6]);

		Assert.That(channel.ActiveOperatorCount, Is.Zero);
		Assert.That(channel.Position.X, Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(
			channel.CurrentVoice!.SoundState.Position.X,
			Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void PersistentVoiceOperatorCommitsNoteVolumeOnlyAtExpiry()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetNoteVolumeSlideCommand(
						4.0,
						TicksPerRow: 6)),
				Event(
					Frame(12),
					new ClearNoteVolumeSlideCommand())),
			new TestResolver((sourceId, new ConstantSound())));

		session.Render(0, 7, new float[7]);

		PlaybackVoice voice =
			session.GetChannelState(0).CurrentVoice!;
		Assert.That(voice.ActiveOperatorCount, Is.EqualTo(1));
		Assert.That(voice.NoteVolume, Is.EqualTo(0.5).Within(1e-12));
		Assert.That(
			voice.GetNoteVolume(6),
			Is.EqualTo(0.65625).Within(1e-12));

		session.Render(7, 6, new float[6]);

		Assert.That(voice.ActiveOperatorCount, Is.Zero);
		Assert.That(voice.NoteVolume, Is.EqualTo(0.8125).Within(1e-12));
		Assert.That(
			voice.GetNoteVolume(12),
			Is.EqualTo(0.8125).Within(1e-12));
	}

	[Test]
	public void PersistentGlobalOperatorsSumDeltasAndCommitPerOriginChannel()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					new StartNoteCommand(sourceId)),
				ChannelEvent(
					Frame(0),
					0,
					new SetGlobalVolumeCommand(0.5),
					new SetGlobalVolumeSlideCommand(
						8.0,
						TicksPerRow: 6)),
				ChannelEvent(
					Frame(0),
					1,
					new SetGlobalVolumeSlideCommand(
						-4.0,
						TicksPerRow: 6)),
				ChannelEvent(
					Frame(12),
					0,
					new ClearGlobalVolumeSlideCommand()),
				ChannelEvent(
					Frame(12),
					1,
					new ClearGlobalVolumeSlideCommand())),
			new TestResolver((sourceId, new ConstantSound())));

		float[] first = new float[7];
		session.Render(0, 7, first);

		Assert.That(session.ActiveGlobalOperatorCount, Is.EqualTo(2));
		Assert.That(session.GlobalVolume, Is.EqualTo(0.5).Within(1e-12));
		Assert.That(first[6], Is.EqualTo(0.578125f).Within(1e-6f));

		session.Render(7, 6, new float[6]);

		Assert.That(session.ActiveGlobalOperatorCount, Is.Zero);
		Assert.That(session.GlobalVolume, Is.EqualTo(0.65625).Within(1e-12));
	}


	private static PlaybackSession Session(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					100,
					new[]
					{
						new OutputChannelConfiguration(
							Vector3.Zero,
							positionalImportance: 0.0),
					})),
			schedule,
			resolver);

	private static NoteSchedule Schedule(params NoteEvent[] events)
	{
		NoteScheduleBuilder builder = new();
		foreach (NoteEvent noteEvent in events)
			builder.Append(noteEvent);
		return builder.Freeze();
	}

	private static NoteEvent Event(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(0),
			commands);

	private static NoteEvent ChannelEvent(
		TimeSpan time,
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private sealed class ConstantSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(NewNotePolicy.Cut);

		public SoundState CreateState()
			=> new TestSoundState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
			=> null;

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
			=> destination.Fill(1.0f);
	}

	private sealed class TestSoundState : SoundState
	{
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<(ObjectId Id, bool Mixdown), ISound> _sounds = [];

		public TestResolver(
			params (ObjectId Id, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, ISound sound) in sounds)
				_sounds.Add((id, false), sound);
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue((sourceId, mixdown), out sound);
	}
}
