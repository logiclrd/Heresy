using System;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class LivePlaybackEventTests
{
	[Test]
	public void LiveStartAndNoteOffAffectSameRunningSession()
	{
		ObjectId sourceId = (ObjectId)7U;
		SampleDefinition definition =
			new(
				sourceId,
				"Loop",
				new ExternalAssetReference("loop.wav"))
			{
				Loop =
					new SampleLoop(
						SampleLoopMode.Forward,
						0,
						2),
			};
		SampleSound sound =
			new(
				definition,
				new MemorySampleData(
					4,
					1,
					new float[] { 1.0f, 2.0f }));
		PlaybackSession session =
			new(
				new RenderContext(
					new RenderConfiguration(
						4,
						new[]
							{
								new OutputChannelConfiguration(
									Vector3.Zero,
									positionalImportance: 0.0),
							})),
				new NoteScheduleBuilder().Freeze(),
				new Resolver(sourceId, sound));
		PlaybackSessionAudioSource source =
			new(session);

		source.EnqueueLiveEvent(
			ChannelTarget.Physical(3),
			[new StartNoteCommand(sourceId)]);
		float[] first = new float[2];
		source.Render(2, first);

		source.EnqueueLiveEvent(
			ChannelTarget.Physical(3),
			[new NoteOffCommand()]);
		float[] second = new float[2];
		source.Render(2, second);

		first.Should().Equal(1.0f, 2.0f);
		second.Should().OnlyContain(value => value == 0.0f);
		session.NextFrame.Should().Be(4);
	}

	[Test]
	public void LiveEventsQueuedBeforeOneRenderPreserveOrder()
	{
		ObjectId sourceId = (ObjectId)7U;
		SampleDefinition definition =
			new(
				sourceId,
				"Loop",
				new ExternalAssetReference("loop.wav"))
			{
				Loop =
					new SampleLoop(
						SampleLoopMode.Forward,
						0,
						1),
			};
		PlaybackSession session =
			new(
				new RenderContext(
					new RenderConfiguration(
						4,
						new[]
							{
								new OutputChannelConfiguration(
									Vector3.Zero,
									positionalImportance: 0.0),
							})),
				new NoteScheduleBuilder().Freeze(),
				new Resolver(
					sourceId,
					new SampleSound(
						definition,
						new MemorySampleData(
							4,
							1,
							new float[] { 1.0f }))));
		PlaybackSessionAudioSource source =
			new(session);

		source.EnqueueLiveEvent(
			ChannelTarget.Physical(0),
			[new StartNoteCommand(sourceId)]);
		source.EnqueueLiveEvent(
			ChannelTarget.Physical(0),
			[new NoteOffCommand()]);
		float[] output = new float[1];

		source.Render(1, output);

		output[0].Should().Be(0.0f);
	}

	private sealed class Resolver : ISoundResolver
	{
		private readonly ObjectId _id;
		private readonly ISound _sound;

		public Resolver(
			ObjectId id,
			ISound sound)
		{
			_id = id;
			_sound = sound;
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
		{
			_ = mixdown;
			sound =
				sourceId == _id
					? _sound
					: null;
			return sound is not null;
		}
	}
}
