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
public sealed class PlaybackTonePortamentoTests
{
	[Test]
	public void ActiveVoiceIsRetargetedWithoutRetriggeringOrChangingSource()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId targetId = (ObjectId)11U;
		SampleSound first = RampSample(500, 1000);
		SampleSound target = ConstantSample(1000.0f, 500, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(firstId)),
				Event(
					Frame(40, 1000),
					new SetTonePortamentoCommand(
						48.0,
						new StartNoteCommand(
							targetId,
							PitchMultiplier: 2.0)))),
			new TestResolver(
				(firstId, false, first),
				(targetId, false, target)));

		float[] before = new float[40];
		session.Render(0, 40, before);

		PlaybackVoice originalVoice =
			session.GetChannelState(0).CurrentVoice!;
		SoundState originalState = originalVoice.SoundState;

		float[] after = new float[30];
		session.Render(40, 30, after);

		PlaybackVoice current =
			session.GetChannelState(0).CurrentVoice!;

		Assert.That(current, Is.SameAs(originalVoice));
		Assert.That(current.SoundState, Is.SameAs(originalState));
		Assert.That(current.Sound, Is.SameAs(first));
		Assert.That(current.StartFrame, Is.EqualTo(0));

		Assert.That(after[0], Is.LessThan(100.0f));
		Assert.That(
			current.SoundState.PitchTrajectory.GetMultiplier(60),
			Is.GreaterThan(1.0));
	}

	[Test]
	public void EmptyChannelUsesTargetNoteAsFallbackStart()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 100, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetTonePortamentoCommand(
						20.0,
						new StartNoteCommand(
							sourceId,
							PitchMultiplier: 2.0)))),
			new TestResolver((sourceId, false, sound)));

		float[] output = new float[1];
		session.Render(0, 1, output);

		PlaybackVoice voice =
			session.GetChannelState(0).CurrentVoice!;

		Assert.That(voice.Sound, Is.SameAs(sound));
		Assert.That(voice.StartFrame, Is.EqualTo(0));
		Assert.That(voice.SoundState.PitchMultiplier, Is.EqualTo(2.0));
		Assert.That(output[0], Is.EqualTo(1.0f));
	}

	[Test]
	public void G00StyleContinuationUsesRememberedTargetAcrossRows()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(1000, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(0, 1000),
					new SetTonePortamentoCommand(
						48.0,
						new StartNoteCommand(
							sourceId,
							PitchMultiplier: 2.0))),
				Event(
					Frame(120, 1000),
					new ClearTonePortamentoCommand()),
				Event(
					Frame(120, 1000),
					new SetTonePortamentoCommand(48.0)),
				Event(
					Frame(240, 1000),
					new ClearTonePortamentoCommand())),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 260, new float[260]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double rowOneEnd = Math.Pow(2.0, 240.0 / 768.0);
		double rowTwoTickOne = Math.Pow(2.0, 280.0 / 768.0);

		Assert.That(
			trajectory.GetMultiplier(120),
			Is.EqualTo(rowOneEnd).Within(1e-12));
		Assert.That(
			trajectory.GetMultiplier(140),
			Is.EqualTo(rowTwoTickOne).Within(1e-12));
	}

	[Test]
	public void TonePortamentoComposesWithVibrato()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(500, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetTonePortamentoCommand(
						48.0,
						new StartNoteCommand(
							sourceId,
							PitchMultiplier: 2.0)),
					new SetVibratoCommand(5, 3))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 30, new float[30]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!.SoundState.PitchTrajectory;

		double porta = Math.Pow(2.0, 40.0 / 768.0);
		double vibrato = TrackerVibrato.GetPitchMultiplier(20, 3);

		Assert.That(
			trajectory.GetMultiplier(20),
			Is.EqualTo(porta * vibrato).Within(1e-12));
	}

	[Test]
	public void SplitRenderingWithTonePortamentoMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(1000, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new StartNoteCommand(sourceId),
				new SetTonePortamentoCommand(
					32.0,
					new StartNoteCommand(
						sourceId,
						PitchMultiplier: 1.5))),
			Event(
				Frame(120, 1000),
				new ClearTonePortamentoCommand()));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession oneSession = Session(1000, schedule, resolver);
		float[] one = new float[180];
		oneSession.Render(0, one.Length, one);

		PlaybackSession splitSession = Session(1000, schedule, resolver);
		float[] split = new float[180];
		splitSession.Render(0, 31, split.AsSpan(0, 31));
		splitSession.Render(31, 39, split.AsSpan(31, 39));
		splitSession.Render(70, 50, split.AsSpan(70, 50));
		splitSession.Render(120, 60, split.AsSpan(120, 60));

		Assert.That(split, Is.EqualTo(one));
	}

	[Test]
	public void HeresyGffSnapsExistingVoiceWithoutResettingItsIntegratedSamplePosition()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(500, 1000);
		PlaybackSession session = Session(1000, Schedule(
			Event(TimeSpan.Zero, new StartNoteCommand(sourceId)),
			Event(Frame(40, 1000), new SetTonePortamentoCommand(
				0xFF * 4.0,
				new StartNoteCommand(sourceId, PitchMultiplier: 2.0),
				Instant: true))), new TestResolver((sourceId, false, sound)));

		session.Render(0, 40, new float[40]);
		PlaybackVoice voice = session.GetChannelState(0).CurrentVoice!;
		SoundState state = voice.SoundState;
		float[] pcm = new float[40];
		session.Render(40, 40, pcm);
		Assert.Multiple(() =>
		{
			Assert.That(session.GetChannelState(0).CurrentVoice, Is.SameAs(voice));
			Assert.That(voice.SoundState, Is.SameAs(state));
			Assert.That(voice.StartFrame, Is.Zero);
			Assert.That(state.PitchTrajectory.GetMultiplier(39), Is.EqualTo(1.0));
			Assert.That(state.PitchTrajectory.GetMultiplier(40), Is.EqualTo(2.0));
			Assert.That(state.PitchTrajectory.GetMultiplier(79), Is.EqualTo(2.0));
			Assert.That(state.PitchTrajectory.GetPosition(40), Is.EqualTo(40.0).Within(1e-10));
			Assert.That(state.PitchTrajectory.GetPosition(41), Is.EqualTo(42.0).Within(1e-10));
		});
	}

	[Test]
	public void FlattenedGffRetunesExistingAndFutureDescendantsButNotUnrelatedVoices()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(500, 1000);
		StartNoteCommand child = new(sourceId)
		{
			ParentSourceScopes = new long[] { 88 },
		};
		PlaybackSession session = Session(1000, Schedule(
			OnChannel(0, 0, new BeginFlattenedSourceVolumeCommand(88, 1.0)),
			OnChannel(1, 0, child),
			OnChannel(3, 0, new StartNoteCommand(sourceId)),
			OnChannel(0, 40, new SetTonePortamentoCommand(
				0xFF * 4.0, new StartNoteCommand(sourceId, PitchMultiplier: 2.0),
				Instant: true)),
			OnChannel(2, 50, child)), new TestResolver((sourceId, false, sound)));

		session.Render(0, 40, new float[40]);
		PlaybackVoice first = session.GetChannelState(1).CurrentVoice!;
		session.Render(40, 40, new float[40]);
		PlaybackVoice later = session.GetChannelState(2).CurrentVoice!;
		PitchTrajectory trajectory = first.SoundState.PitchTrajectory;
		Assert.Multiple(() =>
		{
			Assert.That(session.GetChannelState(1).CurrentVoice, Is.SameAs(first));
			Assert.That(trajectory.GetMultiplier(39), Is.EqualTo(1.0));
			Assert.That(trajectory.GetMultiplier(40), Is.EqualTo(2.0));
			Assert.That(trajectory.GetPosition(40), Is.EqualTo(40.0).Within(1e-10));
			Assert.That(trajectory.GetPosition(41), Is.EqualTo(42.0).Within(1e-10));
			Assert.That(later.SoundState.PitchMultiplier, Is.EqualTo(2.0));
			Assert.That(later.SoundState.PitchTrajectory.GetMultiplier(0), Is.EqualTo(1.0),
				"Future voices must capture the new pitch once, not multiply it twice.");
			Assert.That(session.GetChannelState(3).CurrentVoice!
				.SoundState.PitchTrajectory.GetMultiplier(60), Is.EqualTo(1.0),
				"Sharing the physical session is not scope membership.");
		});
	}

	[Test]
	public void NestedFlattenedGffRatiosComposeAcrossSourceScopesAndRetainHistory()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(500, 1000);
		PlaybackSession session = Session(1000, Schedule(
			OnChannel(0, 0, new BeginFlattenedSourceVolumeCommand(100, 1.0)),
			OnChannel(1, 0, new BeginFlattenedSourceVolumeCommand(200, 1.0)),
			OnChannel(2, 0, new StartNoteCommand(sourceId)
			{
				ParentSourceScopes = new long[] { 100, 200 },
			}),
			OnChannel(0, 20, new SetTonePortamentoCommand(
				1020.0, new StartNoteCommand(sourceId, PitchMultiplier: 2.0),
				Instant: true)),
			OnChannel(1, 40, new SetTonePortamentoCommand(
				1020.0, new StartNoteCommand(sourceId, PitchMultiplier: 1.5),
				Instant: true))), new TestResolver((sourceId, false, sound)));
		session.Render(0, 70, new float[70]);
		PitchTrajectory trajectory = session.GetChannelState(2).CurrentVoice!
			.SoundState.PitchTrajectory;
		Assert.Multiple(() =>
		{
			Assert.That(trajectory.GetMultiplier(19), Is.EqualTo(1.0));
			Assert.That(trajectory.GetMultiplier(20), Is.EqualTo(2.0));
			Assert.That(trajectory.GetMultiplier(39), Is.EqualTo(2.0));
			Assert.That(trajectory.GetMultiplier(40), Is.EqualTo(3.0));
			Assert.That(trajectory.GetPosition(20), Is.EqualTo(20.0).Within(1e-10));
			Assert.That(trajectory.GetPosition(40), Is.EqualTo(60.0).Within(1e-10));
			Assert.That(trajectory.GetPosition(41), Is.EqualTo(63.0).Within(1e-10));
		});
	}

	private static NoteEvent OnChannel(int channel, long frame,
		params NoteCommand[] commands)
		=> new(new MusicalTime(Frame(frame, 1000), 0.0),
			ChannelTarget.Physical(channel), commands);

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
