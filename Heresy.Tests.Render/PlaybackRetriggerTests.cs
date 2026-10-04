using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackRetriggerTests
{
	[Test]
	public void RetriggerRestartsExistingVoiceWithoutReallocation()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(100, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(3, 1000),
					new RetriggerCurrentVoiceCommand(0))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 3, new float[3]);
		PlaybackVoice original =
			session.GetChannelState(0).CurrentVoice!;
		SoundState state = original.SoundState;
		ResonantFilterState filter = original.FilterState;

		session.Render(3, 2, new float[2]);

		PlaybackVoice current =
			session.GetChannelState(0).CurrentVoice!;

		Assert.That(current, Is.SameAs(original));
		Assert.That(current.SoundState, Is.SameAs(state));
		Assert.That(current.FilterState, Is.SameAs(filter));
		Assert.That(current.StartFrame, Is.EqualTo(0));
		Assert.That(current.SoundState.PlaybackOriginFrame, Is.EqualTo(3));
	}

	[Test]
	public void RetriggerVolumeTransformPersistsOnPhysicalChannel()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 100, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(3, 1000),
					new RetriggerCurrentVoiceCommand(0x0B))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 4, new float[4]);

		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(36.0 / 64.0).Within(1e-14));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.NoteVolume,
			Is.EqualTo(36.0 / 64.0).Within(1e-14));
	}

	[Test]
	public void RetriggerUsesBaseVolumeRatherThanMomentaryTremolo()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 100, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetNoteVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetTremoloCommand(5, 8)),
				Event(
					Frame(20, 1000),
					new RetriggerCurrentVoiceCommand(0x09))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 21, new float[21]);

		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(33.0 / 64.0).Within(1e-14));
	}

	[Test]
	public void SplitRenderingWithRetriggerMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(300, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new SetNoteVolumeCommand(0.5),
				new StartNoteCommand(sourceId),
				new SetVibratoCommand(4, 3)),
			Event(
				Frame(40, 1000),
				new RetriggerCurrentVoiceCommand(0x09)),
			Event(
				Frame(80, 1000),
				new RetriggerCurrentVoiceCommand(0x09)));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession oneSession = Session(1000, schedule, resolver);
		float[] one = new float[140];
		oneSession.Render(0, one.Length, one);

		PlaybackSession splitSession = Session(1000, schedule, resolver);
		float[] split = new float[140];
		splitSession.Render(0, 17, split.AsSpan(0, 17));
		splitSession.Render(17, 23, split.AsSpan(17, 23));
		splitSession.Render(40, 40, split.AsSpan(40, 40));
		splitSession.Render(80, 60, split.AsSpan(80, 60));

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
