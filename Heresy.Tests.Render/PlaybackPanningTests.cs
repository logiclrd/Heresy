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
public sealed class PlaybackPanningTests
{
	[Test]
	public void SpatialPositionPersistsToFutureNote()
	{
		ObjectId sourceId = (ObjectId)10U;
		InfiniteTestSound sound = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetSpatialPositionCommand(
						new Vector3(-1.0f, 0.0f, 0.0f))),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[4]);

		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.SoundState.Position,
			Is.EqualTo(new Vector3(-1.0f, 0.0f, 0.0f)));
	}

	[Test]
	public void SpatialPositionUpdatesCurrentVoice()
	{
		ObjectId sourceId = (ObjectId)10U;
		InfiniteTestSound sound = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId)),
				Event(
					Frame(1),
					0,
					new SetSpatialPositionCommand(
						new Vector3(1.0f, 0.0f, 0.0f)))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[4]);

		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.SoundState.Position,
			Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
	}

	[Test]
	public void DisplacedVoiceKeepsOldPositionWhenNewNoteThenPanningShareTimestamp()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(NewNotePolicy.Continue);
		InfiniteTestSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetSpatialPositionCommand(
						new Vector3(-1.0f, 0.0f, 0.0f)),
					new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(secondId),
					new SetSpatialPositionCommand(
						new Vector3(1.0f, 0.0f, 0.0f)))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 2, new float[4]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].SoundState.Position,
			Is.EqualTo(new Vector3(-1.0f, 0.0f, 0.0f)));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.SoundState.Position,
			Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
	}

	private static PlaybackSession Session(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					10,
					new[]
					{
						new OutputChannelConfiguration(
							new Vector3(-1.0f, 0.0f, 0.0f),
							positionalImportance: 1.0),
						new OutputChannelConfiguration(
							new Vector3(1.0f, 0.0f, 0.0f),
							positionalImportance: 1.0),
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
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 10);

	private sealed class InfiniteTestSound : ISound
	{
		private readonly NewNotePolicy _newNotePolicy;

		public InfiniteTestSound(NewNotePolicy newNotePolicy)
			=> _newNotePolicy = newNotePolicy;

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(_newNotePolicy);

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
		{
		}
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
