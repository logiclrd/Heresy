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
public sealed class PlaybackFineSlideTests
{
	[Test]
	public void FinePitchAdjustmentChangesBaseWithoutRemovingVibrato()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(400, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(sourceId),
					new SetVibratoCommand(5, 3)),
				Event(
					Frame(20, 1000),
					new AdjustPitchLinearUnitsCommand(12.0))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 40, new float[40]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double expected =
			Math.Pow(2.0, 12.0 / 768.0)
			* TrackerVibrato.GetPitchMultiplier(20, 3);

		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(expected).Within(1e-12));
	}

	[Test]
	public void FineVolumeAdjustmentPersistsOnPhysicalChannel()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 100, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(20, 1000),
					new AdjustNoteVolumeCommand(4.0))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[30];

		session.Render(0, output.Length, output);

		Assert.That(output[19], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(output[20], Is.EqualTo(0.5625f).Within(1e-6f));
		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(0.5625).Within(1e-12));
	}

	[Test]
	public void FineVolumeAdjustmentOnEmptyChannelAffectsLaterNote()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 100, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new SetNoteVolumeCommand(0.5),
					new AdjustNoteVolumeCommand(-8.0)),
				Event(
					Frame(20, 1000),
					new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[21];

		session.Render(0, output.Length, output);

		Assert.That(output[19], Is.EqualTo(0.0f));
		Assert.That(output[20], Is.EqualTo(0.375f).Within(1e-6f));
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
