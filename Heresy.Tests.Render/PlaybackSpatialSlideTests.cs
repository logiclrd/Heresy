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
public sealed class PlaybackSpatialSlideTests
{
	[Test]
	public void SpatialSlideInterpolatesAtOutputFrameResolution()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetSpatialXSlideCommand(
						0.25,
						TicksPerRow: 4,
						MinimumX: -1.0,
						MaximumX: 1.0)),
				Event(
					Frame(8),
					0,
					new ClearSpatialXSlideCommand())),
			new TestResolver((sourceId, sound)));

		session.Render(0, 8, new float[8]);

		Assert.That(
			sound.ObservedPositions.ConvertAll(p => p.X),
			Is.EqualTo(
				new float[]
				{
					0.0f,
					0.125f,
					0.25f,
					0.375f,
					0.5f,
					0.625f,
					0.75f,
					0.75f,
				}));
	}

	[Test]
	public void ClearingSlideCapturesFinalPositionForFutureNotes()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		PositionObservingSound first = new(NewNotePolicy.Cut);
		PositionObservingSound second = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId),
					new SetSpatialXSlideCommand(
						0.25,
						TicksPerRow: 4,
						MinimumX: -1.0,
						MaximumX: 1.0)),
				Event(
					Frame(8),
					0,
					new ClearSpatialXSlideCommand(),
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 9, new float[9]);

		Assert.That(
			session.GetChannelState(0).Position.X,
			Is.EqualTo(0.75f));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.SoundState.Position.X,
			Is.EqualTo(0.75f));
		Assert.That(second.ObservedPositions[0].X, Is.EqualTo(0.75f));
	}

	[Test]
	public void FineAdjustmentClampsToTrackerSpatialRange()
	{
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetSpatialPositionCommand(
						new Vector3(0.9f, 2.0f, 3.0f)),
					new AdjustSpatialXCommand(
						0.5,
						MinimumX: -1.0,
						MaximumX: 1.0))),
			new TestResolver());

		session.Render(0, 1, new float[1]);

		Assert.That(
			session.GetChannelState(0).Position,
			Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)));
	}

	[Test]
	public void DisplacedVoiceFreezesAtCurrentSlidePosition()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		PositionObservingSound first = new(NewNotePolicy.Continue);
		PositionObservingSound second = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId),
					new SetSpatialXSlideCommand(
						0.25,
						TicksPerRow: 4,
						MinimumX: -1.0,
						MaximumX: 1.0)),
				Event(
					Frame(4),
					0,
					new StartNoteCommand(secondId)),
				Event(
					Frame(8),
					0,
					new ClearSpatialXSlideCommand())),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 9, new float[9]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].SoundState.Position.X,
			Is.EqualTo(0.5f));
		Assert.That(
			second.ObservedPositions.ConvertAll(p => p.X),
			Is.EqualTo(
				new float[]
				{
					0.5f,
					0.625f,
					0.75f,
					0.75f,
					0.75f,
				}));
	}

	[Test]
	public void SplitCallsProduceSameObservedPositionsAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound singleSound = new(NewNotePolicy.Cut);
		PositionObservingSound splitSound = new(NewNotePolicy.Cut);
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetSpatialXSlideCommand(
					0.25,
					TicksPerRow: 4,
					MinimumX: -1.0,
					MaximumX: 1.0)),
			Event(
				Frame(8),
				0,
				new ClearSpatialXSlideCommand()));

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
