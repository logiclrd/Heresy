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
public sealed class SourceFrameSeekCapabilityTests
{
	[Test]
	public void ReplayRequiredMixdownSeekIsStillApplied()
	{
		ObjectId sourceId = (ObjectId)10U;
		SeekableContractSound mixdown =
			new(SourceFrameSeekCost.ReplayRequired);
		ContractResolver resolver = new(
			sourceId,
			flattened: new ContractSound(),
			mixdown: mixdown);

		PlaybackSession session = Session(
			Schedule(
				Event(
					new StartNoteCommand(
						sourceId,
						Mixdown: true),
					new SetSourceFrameOffsetCommand(512))),
			resolver);

		session.Render(0, 1, new float[1]);

		Assert.That(resolver.LastMixdown, Is.True);
		Assert.That(mixdown.LastSourceFrameOffset, Is.EqualTo(512));
		Assert.That(mixdown.RenderCallCount, Is.EqualTo(1));
		Assert.That(mixdown.SeekCost, Is.EqualTo(SourceFrameSeekCost.ReplayRequired));
	}

	[Test]
	public void FlattenedFormMayBeNonSeekableForSameSongObject()
	{
		ObjectId sourceId = (ObjectId)10U;
		ContractSound flattened = new();
		SeekableContractSound mixdown =
			new(SourceFrameSeekCost.ReplayRequired);
		ContractResolver resolver =
			new(sourceId, flattened, mixdown);

		PlaybackSession flattenedSession = Session(
			Schedule(
				Event(
					new StartNoteCommand(
						sourceId,
						Mixdown: false),
					new SetSourceFrameOffsetCommand(256))),
			resolver);

		flattenedSession.Render(0, 1, new float[1]);

		Assert.That(resolver.LastMixdown, Is.False);
		Assert.That(flattened.RenderCallCount, Is.EqualTo(1));
		Assert.That(flattened, Is.Not.InstanceOf<ISourceFrameSeekableSound>());

		PlaybackSession mixdownSession = Session(
			Schedule(
				Event(
					new StartNoteCommand(
						sourceId,
						Mixdown: true),
					new SetSourceFrameOffsetCommand(256))),
			resolver);

		mixdownSession.Render(0, 1, new float[1]);

		Assert.That(resolver.LastMixdown, Is.True);
		Assert.That(mixdown.LastSourceFrameOffset, Is.EqualTo(256));
	}

	[Test]
	public void DirectSeekCostDoesNotChangeSourceFrameSemantics()
	{
		ObjectId sourceId = (ObjectId)10U;
		SeekableContractSound sound =
			new(SourceFrameSeekCost.Direct);
		ContractResolver resolver =
			new(sourceId, sound, sound);

		PlaybackSession session = Session(
			Schedule(
				Event(
					new StartNoteCommand(
						sourceId,
						Mixdown: true),
					new SetSourceFrameOffsetCommand(0x3400))),
			resolver);

		session.Render(0, 1, new float[1]);

		Assert.That(sound.LastSourceFrameOffset, Is.EqualTo(0x3400));
		Assert.That(sound.SeekCost, Is.EqualTo(SourceFrameSeekCost.Direct));
	}

	[Test]
	public void SeekHintsFollowActualBoundSoundCapabilityAndDoNotSuppressOffsets()
	{
		ObjectId id = (ObjectId)10U;
		SeekableContractSound replay = new(SourceFrameSeekCost.ReplayRequired);
		PlaybackSession session = Session(Schedule(Event(
			new StartNoteCommand(id, Mixdown: true),
			new SetSourceFrameOffsetCommand(0x3400),
			new RetriggerCurrentVoiceCommand(0))), new ContractResolver(id,
				new ContractSound(), replay));
		List<(string Operation, long Frame)> hints = [];
		session.ReplayRequiredSeekObserved =
			(operation, frame) => hints.Add((operation, frame));

		session.Render(0, 1, new float[1]);

		Assert.That(hints, Is.EqualTo(new[]
		{
			("Oxx native offset", 0x3400L),
			("Qxy retrigger", 0L),
		}));
		Assert.That(replay.LastSourceFrameOffset, Is.Zero,
			"Qxy legitimately resets the native offset to the beginning.");
	}

	[Test]
	public void DirectAndZeroOffsetNeverProduceAnExpensiveSeekHint()
	{
		ObjectId id = (ObjectId)10U;
		SeekableContractSound direct = new(SourceFrameSeekCost.Direct);
		SeekableContractSound replay = new(SourceFrameSeekCost.ReplayRequired);
		int messages = 0;
		PlaybackSession ordinary = Session(Schedule(Event(
			new StartNoteCommand(id), new SetSourceFrameOffsetCommand(256),
			new RetriggerCurrentVoiceCommand(0))),
			new ContractResolver(id, direct, replay));
		ordinary.ReplayRequiredSeekObserved = (_, _) => messages++;
		ordinary.Render(0, 1, new float[1]);
		PlaybackSession noOffset = Session(Schedule(Event(
			new StartNoteCommand(id, Mixdown: true),
			new SetSourceFrameOffsetCommand(0))),
			new ContractResolver(id, direct, replay));
		noOffset.ReplayRequiredSeekObserved = (_, _) => messages++;
		noOffset.Render(0, 1, new float[1]);
		Assert.That(messages, Is.Zero);
	}

	[Test]
	public void SampleSoundReportsDirectSourceFrameSeeking()
	{
		SampleSound sound = new(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
			new MemorySampleData(
				1000,
				1,
				new float[] { 0.0f, 1.0f }));

		Assert.That(
			((ISourceFrameSeekableSound)sound).SeekCost,
			Is.EqualTo(SourceFrameSeekCost.Direct));
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

	private static NoteEvent Event(params NoteCommand[] commands)
		=> new(
			new MusicalTime(TimeSpan.Zero, 0.0),
			ChannelTarget.Physical(0),
			commands);

	private class ContractSound : ISound
	{
		public int RenderCallCount { get; private set; }

		public virtual NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;

		public virtual SoundState CreateState()
			=> new ContractSoundState();

		public virtual long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
			=> null;

		public virtual void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			RenderCallCount++;
			for (int i = 0; i < destination.Length; i++)
				destination[i] += 1.0f;
		}
	}

	private sealed class SeekableContractSound :
		ContractSound,
		ISourceFrameSeekableSound
	{
		public SeekableContractSound(SourceFrameSeekCost seekCost)
		{
			SeekCost = seekCost;
		}

		public SourceFrameSeekCost SeekCost { get; }

		public long? LastSourceFrameOffset { get; private set; }

		public void SetSourceFrameOffset(
			SoundState state,
			long sourceFrameOffset)
		{
			LastSourceFrameOffset = sourceFrameOffset;

			if (state is ContractSoundState contractState)
				contractState.SourceFrameOffset = sourceFrameOffset;
		}
	}

	private sealed class ContractSoundState : SoundState
	{
		public long SourceFrameOffset { get; set; }
	}

	private sealed class ContractResolver : ISoundResolver
	{
		private readonly ObjectId _sourceId;
		private readonly ISound _flattened;
		private readonly ISound _mixdown;

		public ContractResolver(
			ObjectId sourceId,
			ISound flattened,
			ISound mixdown)
		{
			_sourceId = sourceId;
			_flattened = flattened;
			_mixdown = mixdown;
		}

		public bool? LastMixdown { get; private set; }

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
		{
			LastMixdown = mixdown;

			if (sourceId != _sourceId)
			{
				sound = null;
				return false;
			}

			sound = mixdown ? _mixdown : _flattened;
			return true;
		}
	}
}
