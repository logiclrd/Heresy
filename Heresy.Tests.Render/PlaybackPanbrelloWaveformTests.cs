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
public sealed class PlaybackPanbrelloWaveformTests
{
	[Test]
	public void S51ResetsPhaseAndRampDownStartsAtPositiveMaximum()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TrackerWaveform.Sine,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand(),
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.RampDown)),
				Event(
					Frame(5),
					0,
					new SetPanbrelloCommand(
						15,
						15,
						TrackerWaveform.RampDown,
						TicksPerRow: 2))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 6, new float[6]);

		Assert.That(
			sound.ObservedPositions[5].X,
			Is.EqualTo(120.0f / 128.0f).Within(1e-6f));
	}

	[Test]
	public void S5xAloneResetsPhaseButLeavesHeldOffsetAudible()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TrackerWaveform.Sine,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand()),
				Event(
					Frame(5),
					0,
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Square))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 6, new float[6]);

		Assert.That(
			sound.ObservedPositions[5].X,
			Is.EqualTo(sound.ObservedPositions[4].X)
				.Within(1e-6f));
	}

	[Test]
	public void SquarePanbrelloUsesITSquareTable()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Square),
					new SetPanbrelloCommand(
						64,
						15,
						TrackerWaveform.Square,
						TicksPerRow: 4))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 7, new float[7]);

		Assert.That(
			sound.ObservedPositions.ConvertAll(p => p.X),
			Is.EqualTo(
				new float[]
				{
					120.0f / 128.0f,
					120.0f / 128.0f,
					120.0f / 128.0f,
					60.0f / 128.0f,
					0.0f,
					0.0f,
					0.0f,
				}).Within(1e-6f));
	}

	[Test]
	public void RandomPanbrelloHoldsEachRandomValueForSpeedTicks()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Random),
					new SetPanbrelloCommand(
						3,
						15,
						TrackerWaveform.Random,
						TicksPerRow: 7))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 14, new float[14]);

		float tick0 = sound.ObservedPositions[0].X;
		float tick1 = sound.ObservedPositions[2].X;
		float tick2 = sound.ObservedPositions[4].X;
		float tick3 = sound.ObservedPositions[6].X;
		float tick4 = sound.ObservedPositions[8].X;
		float tick5 = sound.ObservedPositions[10].X;
		float tick6 = sound.ObservedPositions[12].X;

		Assert.That(tick1, Is.EqualTo(tick0).Within(1e-6f));
		Assert.That(tick2, Is.EqualTo(tick0).Within(1e-6f));
		Assert.That(tick4, Is.EqualTo(tick3).Within(1e-6f));
		Assert.That(tick5, Is.EqualTo(tick3).Within(1e-6f));
		Assert.That(tick6, Is.Not.EqualTo(tick3).Within(1e-6f));
	}

	[Test]
	public void SwitchingWaveformResetsRandomHoldPosition()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Random),
					new SetPanbrelloCommand(
						3,
						15,
						TrackerWaveform.Random,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearPanbrelloCommand(),
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Random)),
				Event(
					Frame(5),
					0,
					new SetPanbrelloCommand(
						3,
						15,
						TrackerWaveform.Random,
						TicksPerRow: 2))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 6, new float[6]);

		Assert.That(
			sound.ObservedPositions[5].X,
			Is.Not.EqualTo(sound.ObservedPositions[0].X)
				.Within(1e-6f));
	}

	[Test]
	public void SplitCallsPreserveWaveformPhaseAndHeldOffset()
	{
		ObjectId sourceId = (ObjectId)10U;
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetPanbrelloWaveformCommand(
					TrackerWaveform.RampDown),
				new SetPanbrelloCommand(
					15,
					15,
					TrackerWaveform.RampDown,
					TicksPerRow: 4)),
			Event(
				Frame(8),
				0,
				new ClearPanbrelloCommand()));

		PositionObservingSound singleSound = new();
		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, singleSound)));
		single.Render(0, 10, new float[10]);

		PositionObservingSound splitSound = new();
		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, splitSound)));
		split.Render(0, 3, new float[3]);
		split.Render(3, 4, new float[4]);
		split.Render(7, 3, new float[3]);

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
		public List<Vector3> ObservedPositions { get; } = [];

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
