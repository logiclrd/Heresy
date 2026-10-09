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


	[Test]
	public void VirtualLiveVoicesOverlapAndReleaseIndependently()
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
		SampleSound sound =
			new(
				definition,
				new MemorySampleData(
					4,
					1,
					new float[] { 1.0f }));
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
			ChannelTarget.Virtual(100),
			[new StartNoteCommand(sourceId)]);
		source.EnqueueLiveEvent(
			ChannelTarget.Virtual(101),
			[new StartNoteCommand(sourceId)]);
		float[] both = new float[1];
		source.Render(1, both);

		source.EnqueueLiveEvent(
			ChannelTarget.Virtual(100),
			[new NoteOffCommand()]);
		float[] secondOnly = new float[1];
		source.Render(1, secondOnly);

		source.EnqueueLiveEvent(
			ChannelTarget.Virtual(101),
			[new NoteOffCommand()]);
		float[] released = new float[1];
		source.Render(1, released);

		both[0].Should().Be(2.0f);
		secondOnly[0].Should().Be(1.0f);
		released[0].Should().Be(0.0f);
	}


	[Test]
	public void LiveVirtualNoteOffCutsPreviewVoiceWhenSourceCannotTailOff()
	{
		ObjectId sourceId = (ObjectId)7U;
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
					new InfiniteSound()));
		PlaybackSessionAudioSource source =
			new(session);

		source.EnqueueLiveEvent(
			ChannelTarget.Virtual(42),
			[new StartNoteCommand(sourceId)]);
		float[] sounding = new float[1];
		source.Render(1, sounding);

		source.EnqueueLiveEvent(
			ChannelTarget.Virtual(42),
			[new NoteOffCommand()]);
		float[] released = new float[2];
		source.Render(2, released);

		sounding[0].Should().Be(1.0f);
		// A cut preserves the ordinary anti-click residue for the immediately
		// following frame, but the underlying indefinite voice is gone.
		released[0].Should().Be(1.0f);
		released[1].Should().Be(0.0f);
	}

	[Test]
	public void ScopedVirtualIdsOverlapAndScopedNoteOffAffectsOnlyOwner()
	{
		PlaybackSession session = CreateScopedSession();
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueScopedEvent(10, ChannelTarget.Virtual(7),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueScopedEvent(20, ChannelTarget.Virtual(7),
			[new StartNoteCommand((ObjectId)7U)]);
		float[] both = new float[1];
		source.Render(1, both);
		source.EnqueueScopedEvent(10, ChannelTarget.Virtual(7),
			[new NoteOffCommand()]);
		float[] remaining = new float[1];
		source.Render(1, remaining);
		Assert.That(both[0], Is.EqualTo(2f));
		Assert.That(remaining[0], Is.EqualTo(1f));
	}

	[Test]
	public void ScopedBroadcastTargetsOnlyPreexistingVoicesOfOwner()
	{
		PlaybackSession session = CreateScopedSession();
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueScopedEvent(10, ChannelTarget.Virtual(3),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueScopedEvent(20, ChannelTarget.Virtual(3),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueScopedEvent(10, ChannelTarget.AllVirtualInScope,
			[new NoteOffCommand()]);
		float[] first = new float[1];
		source.Render(1, first);
		// Both starts and the broadcast are frame 0. No voice starts
		// strictly earlier than that broadcast.
		Assert.That(first[0], Is.EqualTo(2f));
		source.EnqueueScopedEvent(10, ChannelTarget.AllVirtualInScope,
			[new NoteOffCommand()]);
		float[] second = new float[1];
		source.Render(1, second);
		Assert.That(second[0], Is.EqualTo(1f));
	}

	[Test]
	public void GlobalVirtualBroadcastTargetsAllOwnersButNotFutureStarts()
	{
		PlaybackSession session = CreateScopedSession();
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueScopedEvent(10, ChannelTarget.Virtual(3),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueScopedEvent(20, ChannelTarget.Virtual(3),
			[new StartNoteCommand((ObjectId)7U)]);
		float[] first = new float[1];
		source.Render(1, first);
		source.EnqueueScopedEvent(10, ChannelTarget.AllVirtual,
			[new NoteOffCommand()]);
		source.EnqueueScopedEvent(30, ChannelTarget.Virtual(3),
			[new StartNoteCommand((ObjectId)7U)]);
		float[] second = new float[1];
		source.Render(1, second);
		Assert.That(first[0], Is.EqualTo(2f));
		Assert.That(second[0], Is.EqualTo(1f));
	}

	[Test]
	public void CancellingScopedVoicesDoesNotCutUnrelatedOwnerOrPreview()
	{
		PlaybackSession session = CreateScopedSession();
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueScopedEvent(10, ChannelTarget.Virtual(5),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueScopedEvent(20, ChannelTarget.Virtual(5),
			[new StartNoteCommand((ObjectId)7U)]);
		source.EnqueueLiveEvent(ChannelTarget.Virtual(5),
			[new StartNoteCommand((ObjectId)7U)]);
		float[] first = new float[1];
		source.Render(1, first);
		source.EnqueueCancelScope(10);
		float[] second = new float[3];
		source.Render(3, second);
		Assert.That(first[0], Is.EqualTo(3f));
		// Cutting the owner retains a one-frame anti-click tail;
		// afterward, only the unrelated owner and legacy preview remain.
		Assert.That(second[0], Is.EqualTo(3f));
		Assert.That(second[1], Is.EqualTo(2f));
		Assert.That(second[2], Is.EqualTo(2f));
	}

	private static PlaybackSession CreateScopedSession()
	{
		ObjectId sourceId = (ObjectId)7U;
		SampleDefinition definition = new(
			sourceId, "Loop", new ExternalAssetReference("loop.wav"))
		{
			Loop = new SampleLoop(SampleLoopMode.Forward, 0, 1),
		};
		return new PlaybackSession(
			new RenderContext(new RenderConfiguration(4,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0.0)])),
			new NoteScheduleBuilder().Freeze(),
			new Resolver(sourceId, new SampleSound(definition,
				new MemorySampleData(4, 1, [1.0f]))));
	}

	private sealed class InfiniteSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;

		public SoundState CreateState()
			=> new InfiniteSoundState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
		{
			_ = context;
			_ = state;
			return null;
		}

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			_ = context;
			_ = state;
			_ = startFrame;
			for (int frame = 0; frame < frameCount; frame++)
				destination[frame] += 1.0f;
		}
	}

	private sealed class InfiniteSoundState : SoundState
	{
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
