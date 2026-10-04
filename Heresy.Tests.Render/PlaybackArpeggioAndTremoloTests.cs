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
public sealed class PlaybackArpeggioAndTremoloTests
{
	[Test]
	public void ArpeggioSwitchesDiscretelyAndClearReturnsToBasePitch()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(600, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetArpeggioCommand(4, 7)),
				Event(
					Frame(120, 1000),
					new ClearArpeggioCommand())),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 140, new float[140]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		Assert.That(trajectory.GetMultiplier(10), Is.EqualTo(1.0));
		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(Math.Pow(2.0, 4.0 / 12.0)).Within(1e-12));
		Assert.That(
			trajectory.GetMultiplier(40),
			Is.EqualTo(Math.Pow(2.0, 7.0 / 12.0)).Within(1e-12));
		Assert.That(trajectory.GetMultiplier(120), Is.EqualTo(1.0));
	}

	[Test]
	public void ArpeggioAndVibratoMultiplyRatherThanOverwriteEachOther()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(600, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetArpeggioCommand(4, 7),
					new SetVibratoCommand(5, 3))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 30, new float[30]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double expected =
			Math.Pow(2.0, 4.0 / 12.0)
			* TrackerVibrato.GetPitchMultiplier(40, 3);

		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(expected).Within(1e-12));
	}

	[Test]
	public void TremoloIsContinuousBetweenTrackerCompatibleAnchors()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 200, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetTremoloCommand(5, 3)),
				Event(
					Frame(60, 1000),
					new ClearTremoloCommand())),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[80];

		session.Render(0, output.Length, output);

		double tick0 =
			0.5 + TrackerTremolo.GetVolumeOffsetUnits(20, 3) / 64.0;
		double tick1 =
			0.5 + TrackerTremolo.GetVolumeOffsetUnits(40, 3) / 64.0;

		Assert.That(output[0], Is.EqualTo((float)tick0).Within(1e-6f));
		Assert.That(output[20], Is.EqualTo((float)tick1).Within(1e-6f));
		Assert.That(output[10], Is.Not.EqualTo(output[0]).Within(1e-6f));
		Assert.That(output[10], Is.Not.EqualTo(output[20]).Within(1e-6f));
		Assert.That(output[60], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void DisplacingTremoloVoiceDoesNotBakeTransientVolumeIntoChannel()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = ConstantSample(
			1.0f,
			200,
			1000,
			NewNotePolicy.Continue);
		SampleSound second = ConstantSample(1.0f, 200, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(firstId),
					new SetTremoloCommand(5, 8)),
				Event(
					Frame(20, 1000),
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));

		float[] output = new float[21];
		session.Render(0, output.Length, output);

		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(0.5).Within(1e-12));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.NoteVolume,
			Is.EqualTo(0.5).Within(1e-12));
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
	}

	[Test]
	public void SplitRenderingWithArpeggioAndTremoloMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(1000, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new SetNoteVolumeCommand(0.75),
				new StartNoteCommand(sourceId),
				new SetArpeggioCommand(3, 7),
				new SetTremoloCommand(4, 5)),
			Event(
				Frame(120, 1000),
				new ClearArpeggioCommand(),
				new ClearTremoloCommand()));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession oneSession = Session(1000, schedule, resolver);
		float[] one = new float[180];
		oneSession.Render(0, one.Length, one);

		PlaybackSession splitSession = Session(1000, schedule, resolver);
		float[] split = new float[180];
		splitSession.Render(0, 17, split.AsSpan(0, 17));
		splitSession.Render(17, 36, split.AsSpan(17, 36));
		splitSession.Render(53, 67, split.AsSpan(53, 67));
		splitSession.Render(120, 60, split.AsSpan(120, 60));

		Assert.That(split, Is.EqualTo(one));
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
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(0),
			commands);

	private static TimeSpan Frame(long frame, int sampleRate)
		=> FrameTime.FrameStartTime(frame, sampleRate);

	private static SampleSound ConstantSample(
		float value,
		int frameCount,
		int sampleRate,
		NewNotePolicy? newNotePolicy = null)
	{
		float[] data = new float[frameCount];
		Array.Fill(data, value);
		return Sound(data, sampleRate, newNotePolicy);
	}

	private static SampleSound RampSample(
		int frameCount,
		int sampleRate)
	{
		float[] data = new float[frameCount];
		for (int i = 0; i < data.Length; i++)
			data[i] = i;
		return Sound(data, sampleRate);
	}

	private static SampleSound Sound(
		float[] data,
		int sampleRate,
		NewNotePolicy? newNotePolicy = null)
		=> new(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
			new MemorySampleData(sampleRate, 1, data),
			newNotePolicy);

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
