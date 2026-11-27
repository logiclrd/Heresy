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
public sealed class PlaybackSampleOffsetTests
{
	[Test]
	public void SourceFrameOffsetUsesNativeFramesWithoutTimeConversion()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(600, 44100);
		PlaybackSession session = Session(
			44100,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetSourceFrameOffsetCommand(256))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[1];

		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(256.0f));
		Assert.That(
			((SampleSoundState)session.GetChannelState(0).CurrentVoice!.SoundState)
				.SourceFrameOffset,
			Is.EqualTo(256));
	}

	[Test]
	public void SampleOffsetChangesNaturalEndFrame()
	{
		SampleSound sound = RampSample(10, 1000);
		RenderContext context = Context(1000);
		SoundState state = sound.CreateState();

		sound.SetSourceFrameOffset(state, 3);

		Assert.That(
			sound.GetEndFrameExclusive(context, state),
			Is.EqualTo(7));
	}

	[Test]
	public void RetriggerClearsSourceFrameOffsetAndRestartsAtZero()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(600, 1000);
		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(sourceId),
					new SetSourceFrameOffsetCommand(256)),
				Event(
					Frame(3, 1000),
					new RetriggerCurrentVoiceCommand(0))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 3, new float[3]);

		PlaybackVoice original =
			session.GetChannelState(0).CurrentVoice!;

		session.Render(3, 1, new float[1]);

		PlaybackVoice current =
			session.GetChannelState(0).CurrentVoice!;
		SampleSoundState state =
			(SampleSoundState)current.SoundState;

		Assert.That(current, Is.SameAs(original));
		Assert.That(state.SourceFrameOffset, Is.EqualTo(0));
		Assert.That(state.PlaybackOriginFrame, Is.EqualTo(3));
	}

	[Test]
	public void GenericPlaybackOffsetAndNativeSourceOffsetAreAdditive()
	{
		SampleSound sound = RampSample(100, 1000);
		RenderContext context = Context(1000);
		SoundState state = sound.CreateState();
		state.PlaybackOffset = TimeSpan.FromMilliseconds(2);
		sound.SetSourceFrameOffset(state, 3);
		float[] output = new float[1];

		sound.Render(context, state, 0, 1, output);

		Assert.That(output[0], Is.EqualTo(5.0f));
	}

	[Test]
	public void SplitRenderingWithSampleOffsetMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(800, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new StartNoteCommand(sourceId),
				new SetSourceFrameOffsetCommand(256)));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession oneSession = Session(1000, schedule, resolver);
		float[] one = new float[100];
		oneSession.Render(0, one.Length, one);

		PlaybackSession splitSession = Session(1000, schedule, resolver);
		float[] split = new float[100];
		splitSession.Render(0, 17, split.AsSpan(0, 17));
		splitSession.Render(17, 31, split.AsSpan(17, 31));
		splitSession.Render(48, 52, split.AsSpan(48, 52));

		Assert.That(split, Is.EqualTo(one));
	}

	[Test]
	public void SeekUsesInstantaneousPitchAndKeepsIntegratedPositionAndNoteAge()
	{
		SampleSound sound = RampSample(2000, 2000);
		RenderContext context = Context(1000);
		SoundState state = sound.CreateState();
		state.PitchMultiplier = 2;
		state.PlaybackSpeedMultiplier = 0.5;
		state.PitchTrajectory.SetMultiplier(0, 1.5);
		state.PitchTrajectory.SetMultiplier(10, 2);
		state.NoteOffTime = TimeSpan.FromSeconds(1);
		// Previous trajectory accumulated 15 at frame 10; current rate
		// is 4000 native frames/sec, not the historical average.
		sound.SeekTime(context, state, 10, TimeSpan.FromMilliseconds(100), false);
		float[] pcm = new float[2];
		sound.Render(context, state, 10, 2, pcm);
		Assert.That(pcm, Is.EqualTo(new float[] { 400, 404 }));
		sound.SeekFrame(context, state, 12, -5, true);
		pcm.AsSpan().Clear();
		sound.Render(context, state, 12, 2, pcm);
		Assert.That(pcm, Is.EqualTo(new float[] { 403, 407 }));
		sound.SeekTime(context, state, 14, TimeSpan.FromMilliseconds(-100), true);
		pcm.AsSpan().Clear();
		sound.Render(context, state, 14, 2, pcm);
		Assert.That(pcm, Is.EqualTo(new float[] { 11, 15 }));
		sound.SeekFrame(context, state, 16, 0, false);
		pcm.AsSpan().Clear();
		sound.Render(context, state, 16, 2, pcm);
		Assert.That(pcm, Is.EqualTo(new float[] { 0, 4 }));
		Assert.That(state.PlaybackOriginFrame, Is.Zero);
		Assert.That(state.NoteOffTime, Is.EqualTo(TimeSpan.FromSeconds(1)));
		Assert.That(sound.GetEndFrameExclusive(context, state), Is.EqualTo(516));
	}

	[Test]
	public void SeekToFiniteEndUsesActualDeadlineNotNoteOrigin()
	{
		SampleSound sound = RampSample(100, 1000);
		RenderContext context = Context(1000);
		SoundState state = sound.CreateState();
		sound.SeekFrame(context, state, 20, 100, false);
		Assert.That(sound.GetEndFrameExclusive(context, state), Is.EqualTo(20));
	}

	[TestCase(1050, -200, 850)]
	[TestCase(1550, -200, 1350)]
	[TestCase(2050, -200, 1350)]
	[TestCase(1550, 500, 1050)]
	[TestCase(100, -200, 0)]
	public void ForwardLoopRelativeSeekUsesUnwrappedCoordinate(long initial, long delta, int expected)
	{
		float[] data = new float[1500];
		for (int i = 0; i < data.Length; i++) data[i] = i;
		SampleDefinition definition = new((ObjectId)1U, "Loop",
			new ExternalAssetReference("loop.raw"));
		definition.Loop = new SampleLoop(SampleLoopMode.Forward, 1000, 1500);
		SampleSound sound = new(definition, new MemorySampleData(1000, 1, data));
		RenderContext context = Context(1000);
		SoundState state = sound.CreateState();
		sound.SeekFrame(context, state, 10, initial, false);
		sound.SeekFrame(context, state, 10, delta, true);
		float[] pcm = new float[1];
		sound.Render(context, state, 10, 1, pcm);
		Assert.That(pcm[0], Is.EqualTo(expected));
	}

	private static RenderContext Context(int sampleRate)
		=> new(
			new RenderConfiguration(
				sampleRate,
				new[]
				{
					new OutputChannelConfiguration(
						Vector3.Zero,
						positionalImportance: 0.0),
				}));

	private static PlaybackSession Session(
		int sampleRate,
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			Context(sampleRate),
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

	private static SampleSound RampSample(
		int frameCount,
		int sampleRate)
	{
		float[] data = new float[frameCount];
		for (int i = 0; i < data.Length; i++)
			data[i] = i;

		return new SampleSound(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
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
