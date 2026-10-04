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
public sealed class PlaybackSessionTests
{
	[Test]
	public void StartNoteRendersResolvedSound()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = Sample(new float[] { 1.0f, 2.0f, 3.0f, 4.0f }, 4);
		PlaybackSession session = Session(
			4,
			Schedule(Event(TimeSpan.Zero, 0, new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[4];

		session.Render(0, 4, output);

		Assert.That(output, Is.EqualTo(new float[] { 1.0f, 2.0f, 3.0f, 4.0f }));
	}

	[Test]
	public void EventBetweenFramesStartsAtNextFrame()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = Sample(new float[] { 5.0f, 6.0f }, 4);
		PlaybackSession session = Session(
			4,
			Schedule(Event(TimeSpan.FromMilliseconds(125), 0, new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[3];

		session.Render(0, 3, output);

		Assert.That(output, Is.EqualTo(new float[] { 0.0f, 5.0f, 6.0f }));
	}

	[Test]
	public void CutImmediatelyDetachesSoundAndContinuesWaveformAsTail()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = Sample(new float[] { 0.0f, 1.0f, 2.0f, 3.0f, 4.0f }, 44100);
		PlaybackSession session = Session(
			44100,
			Schedule(
				Event(Frame(0, 44100), 0, new StartNoteCommand(sourceId)),
				Event(Frame(3, 44100), 0, new NoteCutCommand())),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[4];

		session.Render(0, 4, output);

		Assert.That(output[0], Is.EqualTo(0.0f));
		Assert.That(output[1], Is.EqualTo(1.0f));
		Assert.That(output[2], Is.EqualTo(2.0f));
		Assert.That(output[3], Is.EqualTo(3.0f).Within(1e-6f));
		Assert.That(session.GetChannelState(0).CurrentSound, Is.Null);
		Assert.That(session.GetChannelState(0).AntiClickTail.IsActive, Is.True);
	}

	[Test]
	public void ReplacementNoteMixesOldTailUnderNewSource()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(new float[] { 0.0f, 1.0f, 2.0f }, 44100);
		SampleSound second = Sample(new float[] { 10.0f, 10.0f }, 44100);
		PlaybackSession session = Session(
			44100,
			Schedule(
				Event(Frame(0, 44100), 0, new StartNoteCommand(firstId)),
				Event(Frame(2, 44100), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));
		float[] output = new float[3];

		session.Render(0, 3, output);

		Assert.That(output[0], Is.EqualTo(0.0f));
		Assert.That(output[1], Is.EqualTo(1.0f));
		Assert.That(output[2], Is.EqualTo(12.0f).Within(1e-6f));
	}

	[Test]
	public void NoteOffLeavesSoundAssociatedButSampleEndsAtOffFrame()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleDefinition definition = Definition();
		definition.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 2);
		SampleSound sound = new(
			definition,
			new MemorySampleData(4, 1, new float[] { 1.0f, 2.0f }));
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(Frame(0, 4), 0, new StartNoteCommand(sourceId)),
				Event(Frame(2, 4), 0, new NoteOffCommand())),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[4];

		session.Render(0, 4, output);

		Assert.That(output, Is.EqualTo(new float[] { 1.0f, 2.0f, 0.0f, 0.0f }));
		Assert.That(session.GetChannelState(0).CurrentSound, Is.Null);
	}

	[Test]
	public void NoteAndOverallVolumeMultiply()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = Sample(new float[] { 2.0f }, 1);
		PlaybackSession session = Session(
			1,
			Schedule(Event(
				TimeSpan.Zero,
				0,
				new SetOverallChannelVolumeCommand(0.5),
				new SetNoteVolumeCommand(0.25),
				new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[1];

		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(0.25f).Within(1e-6f));
	}

	[Test]
	public void UnresolvedStartReferenceProducesSilence()
	{
		PlaybackSession session = Session(
			4,
			Schedule(Event(TimeSpan.Zero, 0, new StartNoteCommand((ObjectId)999U))),
			new TestResolver());
		float[] output = new float[2];

		session.Render(0, 2, output);

		Assert.That(output, Is.EqualTo(new float[] { 0.0f, 0.0f }));
	}

	[Test]
	public void SplitCallsProduceSamePcmAsSingleCall()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(new float[] { 0.0f, 1.0f, 2.0f, 3.0f }, 44100);
		SampleSound second = Sample(new float[] { 10.0f, 9.0f, 8.0f }, 44100);
		NoteSchedule schedule = Schedule(
			Event(Frame(0, 44100), 0, new StartNoteCommand(firstId)),
			Event(Frame(2, 44100), 0, new StartNoteCommand(secondId)),
			Event(Frame(5, 44100), 0, new NoteCutCommand()));
		TestResolver resolver = new(
			(firstId, false, first),
			(secondId, false, second));

		PlaybackSession singleSession = Session(44100, schedule, resolver);
		float[] single = new float[8];
		singleSession.Render(0, 8, single);

		PlaybackSession splitSession = Session(44100, schedule, resolver);
		float[] split = new float[8];
		splitSession.Render(0, 3, split.AsSpan(0, 3));
		splitSession.Render(3, 2, split.AsSpan(3, 2));
		splitSession.Render(5, 3, split.AsSpan(5, 3));

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
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static TimeSpan Frame(long frame, int sampleRate)
		=> Heresy.Render.Timing.FrameTime.FrameStartTime(frame, sampleRate);

	private static SampleSound Sample(float[] values, int sampleRate)
		=> new(
			Definition(),
			new MemorySampleData(sampleRate, 1, values));

	private static SampleDefinition Definition()
		=> new(
			(ObjectId)1U,
			"Sample",
			new ExternalAssetReference("sample.raw"));

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
