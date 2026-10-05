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
public sealed class PlaybackFilterTests
{
	[Test]
	public void FilterCommandPersistsOnChannelAndAppliesToCurrentVoice()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 16, 48000);
		PlaybackSession session = Session(
			48000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(sourceId),
					new SetResonantFilterCommand(0.5, 0.25))),
			new TestResolver((sourceId, false, sound)));

		float[] output = new float[1];
		session.Render(0, 1, output);

		PlaybackChannelState channel = session.GetChannelState(0);
		Assert.That(
			channel.FilterParameters,
			Is.EqualTo(new ResonantFilterParameters(0.5, 0.25)));
		Assert.That(
			channel.CurrentVoice!.FilterState.Parameters,
			Is.EqualTo(channel.FilterParameters));
		Assert.That(output[0], Is.LessThan(1.0f));
	}

	[Test]
	public void PartialFilterCommandsPreserveTheOtherParameter()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 16, 48000);
		PlaybackSession session = Session(
			48000,
			Schedule(
				Event(
					Frame(0, 48000),
					new StartNoteCommand(sourceId),
					new SetResonantFilterCommand(0.25, 0.5)),
				Event(
					Frame(1, 48000),
					new SetResonantFilterCutoffCommand(0.75)),
				Event(
					Frame(2, 48000),
					new SetResonantFilterResonanceCommand(0.125))),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 3, new float[3]);

		PlaybackChannelState channel = session.GetChannelState(0);
		Assert.That(
			channel.FilterParameters,
			Is.EqualTo(new ResonantFilterParameters(0.75, 0.125)));
		Assert.That(
			channel.CurrentVoice!.FilterState.Parameters,
			Is.EqualTo(channel.FilterParameters));
	}

	[Test]
	public void ReplacementVoiceGetsSameParametersButFreshFilterHistory()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;

		SampleSound first = ConstantSample(
			1.0f,
			16,
			48000,
			NewNotePolicy.Continue);
		SampleSound second = ConstantSample(
			0.0f,
			16,
			48000);

		PlaybackSession session = Session(
			48000,
			Schedule(
				Event(
					Frame(0, 48000),
					new StartNoteCommand(firstId),
					new SetResonantFilterCommand(0.5, 0.25)),
				Event(
					Frame(2, 48000),
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));

		session.Render(0, 3, new float[3]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));

		PlaybackVoice oldVoice = session.VirtualVoices[0];
		PlaybackVoice newVoice = session.GetChannelState(0).CurrentVoice!;

		Assert.That(
			oldVoice.FilterState.Parameters,
			Is.EqualTo(newVoice.FilterState.Parameters));
		Assert.That(oldVoice.FilterState, Is.Not.SameAs(newVoice.FilterState));

		float[] oldProbe = { 0.0f };
		float[] newProbe = { 0.0f };
		oldVoice.FilterState.ProcessFrame(oldProbe);
		newVoice.FilterState.ProcessFrame(newProbe);

		Assert.That(oldProbe[0], Is.Not.EqualTo(0.0f));
		Assert.That(newProbe[0], Is.EqualTo(0.0f));
	}

	[Test]
	public void MigratedVoiceKeepsItsFilterParametersWhenPhysicalChannelChanges()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;

		SampleSound first = ConstantSample(
			1.0f,
			32,
			48000,
			NewNotePolicy.Continue);
		SampleSound second = ConstantSample(
			1.0f,
			32,
			48000);

		PlaybackSession session = Session(
			48000,
			Schedule(
				Event(
					Frame(0, 48000),
					new StartNoteCommand(firstId),
					new SetResonantFilterCommand(0.25, 0.5)),
				Event(
					Frame(2, 48000),
					new StartNoteCommand(secondId),
					new SetResonantFilterCommand(0.75, 0.1))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));

		session.Render(0, 3, new float[3]);

		Assert.That(
			session.VirtualVoices[0].FilterState.Parameters,
			Is.EqualTo(new ResonantFilterParameters(0.25, 0.5)));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.FilterState.Parameters,
			Is.EqualTo(new ResonantFilterParameters(0.75, 0.1)));
	}

	[Test]
	public void SplitRenderingWithFilterMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(128, 48000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new StartNoteCommand(sourceId),
				new SetResonantFilterCommand(0.55, 0.4)));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession singleSession = Session(48000, schedule, resolver);
		float[] single = new float[80];
		singleSession.Render(0, 80, single);

		PlaybackSession splitSession = Session(48000, schedule, resolver);
		float[] split = new float[80];
		splitSession.Render(0, 17, split.AsSpan(0, 17));
		splitSession.Render(17, 23, split.AsSpan(17, 23));
		splitSession.Render(40, 40, split.AsSpan(40, 40));

		Assert.That(split, Is.EqualTo(single));
	}

	[Test]
	public void NaturalSampleEndIsIndependentOfRenderBlockSize()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 4, 48000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new StartNoteCommand(sourceId),
				new SetResonantFilterCommand(0.5, 0.25)));
		TestResolver resolver = new((sourceId, false, sound));

		PlaybackSession singleSession = Session(48000, schedule, resolver);
		float[] single = new float[8];
		singleSession.Render(0, 8, single);

		PlaybackSession splitSession = Session(48000, schedule, resolver);
		float[] split = new float[8];
		splitSession.Render(0, 2, split.AsSpan(0, 2));
		splitSession.Render(2, 6, split.AsSpan(2, 6));

		Assert.That(split, Is.EqualTo(single));
		Assert.That(single[4], Is.EqualTo(0.0f));
		Assert.That(single[5], Is.EqualTo(0.0f));
		Assert.That(single[6], Is.EqualTo(0.0f));
		Assert.That(single[7], Is.EqualTo(0.0f));
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
		float[] values = new float[frameCount];
		Array.Fill(values, value);

		return new SampleSound(
			Definition(),
			new MemorySampleData(sampleRate, 1, values),
			newNotePolicy);
	}

	private static SampleSound RampSample(
		int frameCount,
		int sampleRate)
	{
		float[] values = new float[frameCount];
		for (int i = 0; i < values.Length; i++)
			values[i] = i / (float)frameCount;

		return new SampleSound(
			Definition(),
			new MemorySampleData(sampleRate, 1, values));
	}

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
