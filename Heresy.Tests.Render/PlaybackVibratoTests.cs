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

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackVibratoTests
{
	[Test]
	public void VibratoIsSmoothBetweenTrackerCompatibleTickAnchors()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				ChannelTarget.Physical(0),
				new StartNoteCommand(sourceId),
				new SetVibratoCommand(5, 3)),
			Event(
				TimeSpan.FromMilliseconds(40),
				ChannelTarget.Physical(0),
				new ClearPitchModulationCommand()));
		PlaybackSession session = Session(
			1000,
			schedule,
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 60, new float[60]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double tick0 = TrackerVibrato.GetPitchMultiplier(20, 3);
		double tick1 = TrackerVibrato.GetPitchMultiplier(40, 3);

		Assert.That(
			trajectory.GetMultiplier(0),
			Is.EqualTo(tick0).Within(1e-14));
		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(tick1).Within(1e-14));

		double between = trajectory.GetMultiplier(10);
		Assert.That(between, Is.Not.EqualTo(tick0).Within(1e-14));
		Assert.That(between, Is.Not.EqualTo(tick1).Within(1e-14));

		Assert.That(trajectory.GetMultiplier(40), Is.EqualTo(1.0));
		Assert.That(
			trajectory.GetPosition(50) - trajectory.GetPosition(40),
			Is.EqualTo(10.0).Within(1e-10));
	}

	[Test]
	public void TempoCommandControlsContinuousVibratoPhaseRate()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				ChannelTarget.Global,
				new SetTempoCommand(250.0)),
			Event(
				TimeSpan.Zero,
				ChannelTarget.Physical(0),
				new StartNoteCommand(sourceId),
				new SetVibratoCommand(5, 3)),
			Event(
				TimeSpan.FromMilliseconds(20),
				ChannelTarget.Physical(0),
				new ClearPitchModulationCommand()));
		PlaybackSession session = Session(
			1000,
			schedule,
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 30, new float[30]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		Assert.That(
			trajectory.GetMultiplier(0),
			Is.EqualTo(TrackerVibrato.GetPitchMultiplier(20, 3)).Within(1e-14));
		Assert.That(
			trajectory.GetMultiplier(10),
			Is.EqualTo(TrackerVibrato.GetPitchMultiplier(40, 3)).Within(1e-14));
		Assert.That(trajectory.GetMultiplier(20), Is.EqualTo(1.0));
	}

	[Test]
	public void ClearingAndReapplyingVibratoPreservesPhase()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				ChannelTarget.Physical(0),
				new StartNoteCommand(sourceId),
				new SetVibratoCommand(5, 3)),
			Event(
				TimeSpan.FromMilliseconds(40),
				ChannelTarget.Physical(0),
				new ClearPitchModulationCommand(),
				new SetVibratoCommand(5, 3)),
			Event(
				TimeSpan.FromMilliseconds(60),
				ChannelTarget.Physical(0),
				new ClearPitchModulationCommand()));
		PlaybackSession session = Session(
			1000,
			schedule,
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 70, new float[70]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		Assert.That(
			trajectory.GetMultiplier(40),
			Is.EqualTo(TrackerVibrato.GetPitchMultiplier(60, 3)).Within(1e-14));
		Assert.That(trajectory.GetMultiplier(60), Is.EqualTo(1.0));
	}

	[Test]
	public void SplitRenderingWithVibratoMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				ChannelTarget.Physical(0),
				new StartNoteCommand(sourceId),
				new SetVibratoCommand(5, 3)),
			Event(
				TimeSpan.FromMilliseconds(80),
				ChannelTarget.Physical(0),
				new ClearPitchModulationCommand()));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession singleSession = Session(1000, schedule, resolver);
		float[] single = new float[120];
		singleSession.Render(0, 120, single);

		PlaybackSession splitSession = Session(1000, schedule, resolver);
		float[] split = new float[120];
		splitSession.Render(0, 17, split.AsSpan(0, 17));
		splitSession.Render(17, 26, split.AsSpan(17, 26));
		splitSession.Render(43, 37, split.AsSpan(43, 37));
		splitSession.Render(80, 40, split.AsSpan(80, 40));

		Assert.That(split, Is.EqualTo(single));
	}

	private static PlaybackSession Session(
		int sampleRate,
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					sampleRate,
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
		ChannelTarget target,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			target,
			commands);

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

		public TestResolver(params (ObjectId Id, bool Mixdown, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, bool mixdown, ISound sound) in sounds)
				_sounds.Add((id, mixdown), sound);
		}

		public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
			=> _sounds.TryGetValue((sourceId, mixdown), out sound);
	}
}
