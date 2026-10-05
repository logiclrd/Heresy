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
	public void PitchSlideDomainCompressesSmoothlyDuringTempoRamp()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetPitchSlideCommand(
						TrackerVibrato.LinearSlideUnitsPerOctave / 4.0,
						TicksPerRow: 6)),
				Global(
					0,
					new SetTempoRampCommand(
						250.0,
						TrackerTicks: 1.0))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[2]);

		double trackerTicksAtFrameOne =
			Math.Exp(0.5) - 1.0;
		double expected =
			Math.Pow(
				2.0,
				trackerTicksAtFrameOne / 4.0);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(1);

		Assert.That(
			actual,
			Is.EqualTo(expected).Within(1e-12));
	}

	[Test]
	public void EffectStartedAfterTempoRampRenormalizesToUnitWallTimeRate()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		PlaybackSession session = Session(
			Schedule(
				Global(
					0,
					new SetTempoRampCommand(
						250.0,
						TrackerTicks: 1.0)),
				Event(
					2,
					0,
					new StartNoteCommand(sourceId),
					new SetPitchSlideCommand(
						TrackerVibrato.LinearSlideUnitsPerOctave / 4.0,
						TicksPerRow: 6))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 4, new float[4]);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(1);

		Assert.That(
			actual,
			Is.EqualTo(Math.Pow(2.0, 0.25))
				.Within(1e-12));
	}

	[Test]
	public void TonePortamentoUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = LongRampSample();
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetTonePortamentoCommand(
						TrackerVibrato.LinearSlideUnitsPerOctave / 2.0,
						new StartNoteCommand(sourceId, 2.0),
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 4, new float[4]);

		double actual =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(3);

		Assert.That(actual, Is.EqualTo(2.0).Within(1e-12));
	}

	[Test]
	public void NoteVolumeSlideUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetNoteVolumeSlideCommand(
						-16.0,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, new ConstantSound())));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(output[3], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void ChannelVolumeSlideUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetOverallChannelVolumeSlideCommand(
						-16.0,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, new ConstantSound())));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(output[3], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void SpatialSlideUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetSpatialXSlideCommand(
						0.25,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 4, new float[4]);

		Assert.That(
			sound.ObservedPositions[3].X,
			Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void TremorUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetTremorCommand(
						1,
						1,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, new ConstantSound())));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(
			output,
			Is.EqualTo(new float[] { 1, 1, 0, 1 }));
	}

	[Test]
	public void GlobalVolumeSlideUsesSharedTickClock()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetGlobalVolumeSlideCommand(
						-16.0,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, new ConstantSound())));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(output[3], Is.EqualTo(0.75f).Within(1e-6f));
	}

	[Test]
	public void VibratoAndTremoloMatchReferenceAtSameTickPosition()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new();

		PlaybackSession changed = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetVibratoCommand(5, 8),
					new SetTremoloCommand(5, 8)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, sound)));
		float[] changedOutput = new float[4];
		changed.Render(0, 4, changedOutput);

		PlaybackSession reference = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetVibratoCommand(5, 8),
					new SetTremoloCommand(5, 8))),
			new TestResolver((sourceId, sound)));
		float[] referenceOutput = new float[5];
		reference.Render(0, 5, referenceOutput);

		double changedPitch =
			changed.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(3);
		double referencePitch =
			reference.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory
				.GetMultiplier(4);

		Assert.That(
			changedPitch,
			Is.EqualTo(referencePitch).Within(1e-12));
		Assert.That(
			changedOutput[3],
			Is.EqualTo(referenceOutput[4]).Within(1e-5f));
	}

	[Test]
	public void PanbrelloMatchesReferenceAtSameTickPosition()
	{
		ObjectId sourceId = (ObjectId)10U;
		PositionObservingSound changedSound = new();
		PlaybackSession changed = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 6)),
				Global(2, new SetTempoCommand(250))),
			new TestResolver((sourceId, changedSound)));
		changed.Render(0, 4, new float[4]);

		PositionObservingSound referenceSound = new();
		PlaybackSession reference = Session(
			Schedule(
				Event(
					0,
					0,
					new StartNoteCommand(sourceId),
					new SetPanbrelloCommand(
						15,
						15,
						TicksPerRow: 6))),
			new TestResolver((sourceId, referenceSound)));
		reference.Render(0, 5, new float[5]);

		Assert.That(
			changedSound.ObservedPositions[3].X,
			Is.EqualTo(referenceSound.ObservedPositions[4].X)
				.Within(1e-6f));
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

	private sealed class ConstantSound : ISound
	{
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
			=> destination.Fill(1.0f);
	}

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
