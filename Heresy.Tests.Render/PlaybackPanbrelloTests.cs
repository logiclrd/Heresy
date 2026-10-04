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
public sealed class PlaybackPanbrelloTests
{
	private const float Tick1Offset = 43.0f / 128.0f;
	private const float Tick2Offset = 80.0f / 128.0f;

	[Test]
	public void PanbrelloInterpolatesSmoothlyBetweenExactITTickAnchors()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 4)),
				Event(
					Frame(8),
					0,
					new ClearPanbrelloCommand())),
			new TestResolver((sourceId, sound)));

		session.Render(0, 5, new float[5]);

		Assert.That(
			sound.ObservedPositions.ConvertAll(p => p.X),
			Is.EqualTo(
				new float[]
				{
					0.0f,
					Tick1Offset / 2.0f,
					Tick1Offset,
					(Tick1Offset + Tick2Offset) / 2.0f,
					Tick2Offset,
				}).Within(1e-6f));
	}

	[Test]
	public void RowEndFreezesLastPanbrelloOffsetInsteadOfReturningToBase()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand())),
			new TestResolver((sourceId, sound)));

		session.Render(0, 6, new float[6]);

		Assert.That(
			sound.ObservedPositions.ConvertAll(p => p.X),
			Is.EqualTo(
				new float[]
				{
					0.0f,
					Tick1Offset / 2.0f,
					Tick1Offset,
					Tick1Offset,
					Tick1Offset,
					Tick1Offset,
				}).Within(1e-6f));
	}

	[Test]
	public void ExplicitPanningClearsHeldPanbrelloOffset()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand()),
				Event(
					Frame(5),
					0,
					new SetSpatialPositionCommand(
						new Vector3(
							0.5f,
							0.0f,
							0.0f)))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 6, new float[6]);

		Assert.That(
			sound.ObservedPositions[4].X,
			Is.EqualTo(Tick1Offset).Within(1e-6f));
		Assert.That(
			sound.ObservedPositions[5].X,
			Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void NewNoteClearsHeldOffsetButNnaVoiceKeepsItsInstantaneousPosition()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		PositionObservingSound first =
			new(NewNotePolicy.Continue);
		PositionObservingSound second =
			new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand()),
				Event(
					Frame(5),
					0,
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 6, new float[6]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].SoundState.Position.X,
			Is.EqualTo(Tick1Offset).Within(1e-6f));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.SoundState.Position.X,
			Is.EqualTo(0.0f).Within(1e-6f));
		Assert.That(
			second.ObservedPositions[0].X,
			Is.EqualTo(0.0f).Within(1e-6f));
	}

	[Test]
	public void SplitCallsProduceSameObservedPositionsAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound singleSound =
			new(NewNotePolicy.Cut);
		PositionObservingSound splitSound =
			new(NewNotePolicy.Cut);
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetPanbrelloCommand(
					15,
					15,
					TicksPerRow: 4)),
			Event(
				Frame(8),
				0,
				new ClearPanbrelloCommand()));

		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, singleSound)));
		single.Render(0, 8, new float[8]);

		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, splitSound)));
		split.Render(0, 3, new float[3]);
		split.Render(3, 2, new float[2]);
		split.Render(5, 3, new float[3]);

		Assert.That(
			splitSound.ObservedPositions,
			Is.EqualTo(singleSound.ObservedPositions));
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
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private sealed class PositionObservingSound : ISound
	{
		private readonly NewNotePolicy _policy;

		public PositionObservingSound(NewNotePolicy policy)
			=> _policy = policy;

		public List<Vector3> ObservedPositions { get; } = [];

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(_policy);

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
			for (int frame = 0; frame < frameCount; frame++)
				ObservedPositions.Add(state.Position);
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
