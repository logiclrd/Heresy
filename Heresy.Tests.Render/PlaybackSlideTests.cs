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
public sealed class PlaybackSlideTests
{
	[Test]
	public void NoteVolumeSlideSpreadsLegacyRowDeltaAcrossWholeRow()
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
					new SetNoteVolumeSlideCommand(4.0)),
				Event(
					TimeSpan.FromMilliseconds(120),
					new ClearNoteVolumeSlideCommand())),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[140];

		session.Render(0, output.Length, output);

		Assert.That(output[0], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(output[10], Is.EqualTo(0.5260417f).Within(1e-6f));
		Assert.That(output[20], Is.EqualTo(0.5520833f).Within(1e-6f));
		Assert.That(output[100], Is.EqualTo(0.7604167f).Within(1e-6f));
		Assert.That(output[119], Is.EqualTo(0.8098958f).Within(1e-6f));
		Assert.That(output[120], Is.EqualTo(0.8125f).Within(1e-6f));
		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(0.8125).Within(1e-12));
	}

	[Test]
	public void PitchSlideSpreadsLegacyRowDeltaAndClearCommitsResult()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(400, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetPitchSlideCommand(48.0)),
				Event(
					TimeSpan.FromMilliseconds(120),
					new ClearPitchSlideCommand())),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 140, new float[140]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double finalMultiplier = Math.Pow(2.0, 240.0 / 768.0);

		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(Math.Pow(2.0, 40.0 / 768.0)).Within(1e-14));
		Assert.That(
			trajectory.GetMultiplier(100),
			Is.EqualTo(finalMultiplier).Within(1e-14));
		Assert.That(
			trajectory.GetMultiplier(120),
			Is.EqualTo(finalMultiplier).Within(1e-14));
		Assert.That(
			trajectory.GetMultiplier(130),
			Is.EqualTo(finalMultiplier).Within(1e-14));
	}

	[Test]
	public void PitchSlideAndVibratoMultiplyRatherThanOverwriteEachOther()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(400, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetPitchSlideCommand(48.0),
					new SetVibratoCommand(5, 3)),
				Event(
					TimeSpan.FromMilliseconds(120),
					new ClearPitchModulationCommand(),
					new ClearPitchSlideCommand())),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 130, new float[130]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double slideAtTick1 = Math.Pow(2.0, 40.0 / 768.0);
		double vibratoAtTick1 = TrackerVibrato.GetPitchMultiplier(20, 3);

		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(slideAtTick1 * vibratoAtTick1).Within(1e-12));
	}

	[Test]
	public void SplitRenderingWithSlidesMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(500, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new SetNoteVolumeCommand(0.75),
				new StartNoteCommand(sourceId),
				new SetPitchSlideCommand(-32.0),
				new SetNoteVolumeSlideCommand(-2.0)),
			Event(
				TimeSpan.FromMilliseconds(120),
				new ClearPitchSlideCommand(),
				new ClearNoteVolumeSlideCommand()));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession single = Session(1000, schedule, resolver);
		float[] one = new float[180];
		single.Render(0, one.Length, one);

		PlaybackSession split = Session(1000, schedule, resolver);
		float[] many = new float[180];
		split.Render(0, 37, many.AsSpan(0, 37));
		split.Render(37, 41, many.AsSpan(37, 41));
		split.Render(78, 42, many.AsSpan(78, 42));
		split.Render(120, 60, many.AsSpan(120, 60));

		Assert.That(many, Is.EqualTo(one));
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

	private static SampleSound ConstantSample(
		float value,
		int frameCount,
		int sampleRate)
	{
		float[] data = new float[frameCount];
		Array.Fill(data, value);
		return Sound(data, sampleRate);
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

	private static SampleSound Sound(float[] data, int sampleRate)
		=> new(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
			new MemorySampleData(sampleRate, 1, data));

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
