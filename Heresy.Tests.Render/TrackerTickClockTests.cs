using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerTickClockTests
{
	[Test]
	public void PiecewiseTempoMapIntegratesFractionalTicksAcrossChanges()
	{
		NoteSchedule schedule = Schedule(
			Global(Frame(2), new SetTempoCommand(250)),
			Global(Frame(5), new SetTempoCommand(100)));

		TrackerTickClock clock =
			new(schedule, sampleRate: 100);

		Assert.That(
			clock.GetElapsedTicks(0, 2),
			Is.EqualTo(1.0).Within(1e-12));
		Assert.That(
			clock.GetElapsedTicks(0, 5),
			Is.EqualTo(4.0).Within(1e-12));
		Assert.That(
			clock.GetElapsedTicks(2, 6),
			Is.EqualTo(3.4).Within(1e-12));
	}

	[Test]
	public void SameFrameTempoCommandsRespectScheduleOrder()
	{
		NoteSchedule schedule = Schedule(
			Event(
				Frame(2),
				0,
				new SetTempoCommand(200)),
			Event(
				Frame(2),
				1,
				new SetTempoCommand(250)));

		TrackerTickClock clock =
			new(schedule, sampleRate: 100);

		Assert.That(
			clock.GetElapsedTicks(2, 3),
			Is.EqualTo(1.0).Within(1e-12));
	}

	[Test]
	public void PitchSlideRetimesAtMidEffectTempoChange()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 100);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetPitchSlideCommand(
						TrackerVibrato.LinearSlideUnitsPerOctave / 4.0,
						TicksPerRow: 6)),
				Global(
					Frame(2),
					new SetTempoCommand(250))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 4, new float[4]);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(3);

		Assert.That(
			actual,
			Is.EqualTo(
				Math.Pow(
					2.0,
					320.0
						/ TrackerVibrato.LinearSlideUnitsPerOctave))
				.Within(1e-12));
	}

	[Test]
	public void ArpeggioDiscreteTickIndexFollowsMidEffectTempoChange()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 100);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetArpeggioCommand(4, 7)),
				Global(
					Frame(2),
					new SetTempoCommand(250))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 4, new float[4]);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(3);

		Assert.That(
			actual,
			Is.EqualTo(Math.Pow(2.0, 7.0 / 12.0))
				.Within(1e-12));
	}

	[Test]
	public void SplitRenderObservesSameTickClockAsSingleRender()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 100);
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetPitchSlideCommand(
					TrackerVibrato.LinearSlideUnitsPerOctave / 8.0,
					TicksPerRow: 8)),
			Global(Frame(2), new SetTempoCommand(250)),
			Global(Frame(5), new SetTempoCommand(100)));

		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		single.Render(0, 7, new float[7]);
		double singleValue =
			single.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(6);

		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		split.Render(0, 2, new float[2]);
		split.Render(2, 3, new float[3]);
		split.Render(5, 2, new float[2]);
		double splitValue =
			split.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(6);

		Assert.That(splitValue, Is.EqualTo(singleValue).Within(1e-14));
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

	private static NoteEvent Global(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Global,
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private static SampleSound LongRampSample(int sampleRate)
	{
		float[] data = new float[400];
		for (int i = 0; i < data.Length; i++)
			data[i] = i;

		return new SampleSound(
			new SampleDefinition(
				(ObjectId)1U,
				"Ramp",
				new ExternalAssetReference("ramp.raw")),
			new MemorySampleData(sampleRate, 1, data));
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
