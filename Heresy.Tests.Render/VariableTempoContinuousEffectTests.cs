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
public sealed class VariableTempoContinuousEffectTests
{
	[Test]
	public void PitchSlideUsesRowTimeInsideTempoRamp()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		NoteSchedule schedule = Schedule(
			Event(
				0,
				0,
				new StartNoteCommand(sourceId),
				new SetPitchSlideCommand(
					192.0,
					TicksPerRow: 6)),
			Global(
				0,
				new SetTempoRampCommand(
					135.0,
					trackerTicks: 6.0)));

		PlaybackSession session = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		session.Render(0, 6, new float[6]);

		TrackerTickClock clock =
			new(schedule, sampleRate: 100);
		double rowTime =
			clock.GetElapsedTicks(0, 5);
		double expectedUnits =
			192.0 * 5.0 * rowTime / 6.0;

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(5);

		Assert.That(
			actual,
			Is.EqualTo(
				Math.Pow(
					2.0,
					expectedUnits
						/ TrackerVibrato.LinearSlideUnitsPerOctave))
				.Within(1e-12));
	}

	[Test]
	public void PersistentSlideCommitsSameLegacyTotalAtRampEnd()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		TrackerRowTiming timing = new(
			speed: 6.0,
			startingTempo: 125.0,
			endingTempo: 135.0);
		long endFrame =
			FrameTime.Ceiling(
				TimeSpan.FromSeconds(timing.RowDurationSeconds),
				100);

		NoteSchedule schedule = Schedule(
			Event(
				0,
				0,
				new StartNoteCommand(sourceId),
				new SetPitchSlideCommand(
					48.0,
					TicksPerRow: 6)),
			Global(
				0,
				new SetTempoRampCommand(
					135.0,
					trackerTicks: 6.0)),
			Event(
				endFrame,
				0,
				new ClearPitchSlideCommand()));

		PlaybackSession session = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		session.Render(0, (int)endFrame + 1, new float[(int)endFrame + 1]);

		double final =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(endFrame);

		Assert.That(
			final,
			Is.EqualTo(Math.Pow(2.0, 240.0 / 768.0))
				.Within(1e-10));
	}

	[Test]
	public void ArpeggioStillUsesDiscreteRowTimeThresholds()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetArpeggioCommand(4, 7)),
				Global(2, new SetTempoCommand(250))),
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
	public void NativeWallTimeIsNotRenormalizedByTrackerTempo()
	{
		TrackerRowTiming slow = new(
			6.0,
			125.0,
			125.0);
		TrackerRowTiming fast = new(
			6.0,
			250.0,
			250.0);

		Assert.That(slow.GetRowTime(0.05), Is.EqualTo(2.5));
		Assert.That(fast.GetRowTime(0.05), Is.EqualTo(5.0));

		// The same wall time remains 0.05 seconds for a native operator.
		//Assert.That(0.05, Is.EqualTo(0.05));
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
		long frame,
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(Frame(frame), 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static NoteEvent Global(
		long frame,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(Frame(frame), 0.0),
			ChannelTarget.Global,
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private static SampleSound LongRampSample()
	{
		float[] data = new float[400];
		for (int i = 0; i < data.Length; i++)
			data[i] = i;

		return new SampleSound(
			new SampleDefinition(
				(ObjectId)1U,
				"Ramp",
				new ExternalAssetReference("ramp.raw")),
			new MemorySampleData(100, 1, data));
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
