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
public sealed class PlaybackFineVibratoTests
{
	[Test]
	public void FineVibratoHasOneQuarterNormalLinearPitchExcursion()
	{
		ObjectId normalId = (ObjectId)10U;
		ObjectId fineId = (ObjectId)11U;
		SampleSound sound = LongRampSample(sampleRate: 1000);

		PlaybackSession normal = Session(
			Schedule(
				Event(
					TimeSpan.Zero,
					0,
					new StartNoteCommand(normalId),
					new SetVibratoCommand(5, 8))),
			new TestResolver((normalId, sound)));

		PlaybackSession fine = Session(
			Schedule(
				Event(
					TimeSpan.Zero,
					0,
					new StartNoteCommand(fineId),
					new SetVibratoCommand(
						5,
						8,
						TrackerWaveform.Sine,
						DepthScale: 0.25))),
			new TestResolver((fineId, sound)));

		normal.Render(0, 11, new float[11]);
		fine.Render(0, 11, new float[11]);

		double normalMultiplier =
			normal.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(10);
		double fineMultiplier =
			fine.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(10);

		double normalUnits =
			Math.Log2(normalMultiplier)
				* TrackerVibrato.LinearSlideUnitsPerOctave;
		double fineUnits =
			Math.Log2(fineMultiplier)
				* TrackerVibrato.LinearSlideUnitsPerOctave;

		Assert.That(
			fineUnits,
			Is.EqualTo(normalUnits / 4.0).Within(1e-12));
	}

	[Test]
	public void SwitchingBetweenNormalAndFineVibratoPreservesPhase()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample(sampleRate: 1000);
		PlaybackSession session = Session(
			Schedule(
				Event(
					TimeSpan.Zero,
					0,
					new StartNoteCommand(sourceId),
					new SetVibratoCommand(5, 4)),
				Event(
					TimeSpan.FromMilliseconds(20),
					0,
					new ClearPitchModulationCommand(),
					new SetVibratoCommand(
						5,
						4,
						TrackerWaveform.Sine,
						DepthScale: 0.25))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 21, new float[21]);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(20);

		double expectedUnits =
			TrackerVibrato.GetLinearSlideUnits(
				TrackerWaveform.Sine,
				phase: 20,
				depth: 4)
				* 0.25;
		double expected =
			Math.Pow(
				2.0,
				expectedUnits
					/ TrackerVibrato.LinearSlideUnitsPerOctave);

		Assert.That(actual, Is.EqualTo(expected).Within(1e-14));
	}

	private static PlaybackSession Session(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					1000,
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

		public TestResolver(params (ObjectId Id, ISound Sound)[] sounds)
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
