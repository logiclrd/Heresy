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
public sealed class PlaybackSessionTests
{
	[Test]
	public void FlattenedScopeCutIncludesVirtualAndDisplacedNnaVoicesButNotHostSiblings()
	{
		ObjectId sampleId = (ObjectId)10U;
		float[] waveform = new float[64];
		Array.Fill(waveform, 1f);
		PlaybackSession session = Session(1000,
			Schedule(), new TestResolver(
				(sampleId, false,
					Sample(waveform, 1000, NewNotePolicy.Continue))));
		session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
			[new BeginFlattenedSourceVolumeCommand(1, 0.5)]);
		StartNoteCommand nestedStart = new(sampleId)
		{
			ParentSourceScopes = new long[] { 1 },
		};
		session.ApplyScopedEvent(100, ChannelTarget.Virtual(7),
			[nestedStart]);
		session.ApplyScopedEvent(100, ChannelTarget.Physical(1),
			[nestedStart], physicalPlaybackOwner: 1);
		// The old scoped physical voice becomes a displaced NNA voice.
		session.ApplyScopedEvent(100, ChannelTarget.Physical(1),
			[nestedStart], physicalPlaybackOwner: 1);
		// An unrelated root physical host must survive the scope action.
		session.ApplyScopedEvent(200, ChannelTarget.Physical(2),
			[new StartNoteCommand(sampleId)]);
		float[] first = new float[1];
		session.Render(0, 1, first);
		Assert.That(first[0], Is.EqualTo(2.5f).Within(1e-6f));
		Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));

		session.ApplyFlattenedScopeAction(1, NoteDisplacementAction.Cut);
		Assert.That(session.VirtualVoices, Is.Empty);
		Assert.That(session.GetChannelState(2).CurrentVoice, Is.Not.Null);
		float[] after = new float[30];
		session.Render(1, after.Length, after);
		Assert.That(after[20], Is.EqualTo(1f).Within(1e-6f),
			"Only the unrelated host remains after the cut tails decay.");
	}

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
	public void ReplacementNoteWithDefaultCutMixesOldTailUnderNewSource()
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
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(0));
	}

	[Test]
	public void ContinueMigratesOldVoiceAndLeavesItPlaying()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(
			new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
			4,
			NewNotePolicy.Continue);
		SampleSound second = Sample(
			new float[] { 10.0f, 10.0f, 10.0f },
			4);
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(Frame(0, 4), 0, new StartNoteCommand(firstId)),
				Event(Frame(2, 4), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));
		float[] output = new float[3];

		session.Render(0, 3, output);

		Assert.That(output, Is.EqualTo(new float[] { 1.0f, 1.0f, 11.0f }));
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(session.VirtualVoices[0].Configuration.NewNotePolicy.Action, Is.EqualTo(NewNoteAction.Continue));
	}

	[Test]
	public void AllVirtualBroadcastReachesDisplacedNnaVoicesButScopedDoesNot()
	{
		ObjectId first = (ObjectId)10U;
		ObjectId second = (ObjectId)11U;
		PlaybackSession session = Session(4,
			Schedule(
				Event(Frame(0, 4), 0, new StartNoteCommand(first)),
				Event(Frame(2, 4), 0, new StartNoteCommand(second))),
			new TestResolver(
				(first, false, Sample(
					[1f, 1f, 1f, 1f, 1f], 4, NewNotePolicy.Continue)),
				(second, false, Sample([10f, 10f, 10f], 4))));
		float[] buffer = new float[3];
		session.Render(0, 3, buffer);
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		session.ApplyScopedEvent(33, ChannelTarget.AllVirtualInScope,
			[new NoteCutCommand()]);
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		session.ApplyScopedEvent(33, ChannelTarget.AllVirtual,
			[new NoteCutCommand()]);
		Assert.That(session.VirtualVoices.Count, Is.Zero);
		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null);
	}

	[Test]
	public void OffMigratesOldVoiceAndSetsItsNoteOffTime()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ReleasingTestSound first = new(
			NewNotePolicy.Off,
			releaseFrames: 2);
		SampleSound second = Sample(
			new float[] { 10.0f, 10.0f, 10.0f, 10.0f },
			4);
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(Frame(0, 4), 0, new StartNoteCommand(firstId)),
				Event(Frame(2, 4), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));
		float[] output = new float[5];

		session.Render(0, 5, output);

		Assert.That(
			output,
			Is.EqualTo(new float[] { 1.0f, 1.0f, 11.0f, 11.0f, 10.0f }));
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(0));
	}

	[Test]
	public void FadeMigratesOldVoiceAndRampsToZeroOverConfiguredDuration()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(
			new float[]
			{
				1.0f, 1.0f, 1.0f, 1.0f, 1.0f,
				1.0f, 1.0f, 1.0f, 1.0f, 1.0f,
			},
			10,
			NewNotePolicy.Fade(TimeSpan.FromMilliseconds(200)));
		SampleSound second = Sample(
			new float[] { 10.0f, 10.0f, 10.0f, 10.0f },
			10);
		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0, 10), 0, new StartNoteCommand(firstId)),
				Event(Frame(2, 10), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));
		float[] output = new float[5];

		session.Render(0, 5, output);

		Assert.That(output[0], Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(output[1], Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(output[2], Is.EqualTo(11.0f).Within(1e-6f));
		Assert.That(output[3], Is.EqualTo(10.5f).Within(1e-6f));
		Assert.That(output[4], Is.EqualTo(10.0f).Within(1e-6f));
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(0));
	}

	[Test]
	public void NewNotePolicyIsSnapshottedWhenVoiceStarts()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(
			new float[] { 1.0f, 1.0f, 1.0f, 1.0f },
			4,
			NewNotePolicy.Continue);
		SampleSound second = Sample(
			new float[] { 10.0f, 10.0f },
			4);
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(Frame(0, 4), 0, new StartNoteCommand(firstId)),
				Event(Frame(2, 4), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));

		float[] firstBlock = new float[2];
		session.Render(0, 2, firstBlock);

		first.NewNotePolicy = NewNotePolicy.Cut;

		float[] secondBlock = new float[1];
		session.Render(2, 1, secondBlock);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].Configuration.NewNotePolicy.Action,
			Is.EqualTo(NewNoteAction.Continue));
	}

	[Test]
	public void MigratedVoiceKeepsVolumesCapturedFromPhysicalChannel()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		SampleSound first = Sample(
			new float[] { 2.0f, 2.0f, 2.0f },
			4,
			NewNotePolicy.Continue);
		SampleSound second = Sample(
			new float[] { 0.0f },
			4);
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					0,
					new SetOverallChannelVolumeCommand(0.5),
					new SetNoteVolumeCommand(0.25),
					new StartNoteCommand(firstId)),
				Event(Frame(1, 4), 0, new StartNoteCommand(secondId)),
				Event(Frame(1, 4), 0, new SetOverallChannelVolumeCommand(1.0))),
			new TestResolver(
				(firstId, false, first),
				(secondId, false, second)));
		float[] output = new float[2];

		session.Render(0, 2, output);

		Assert.That(output[0], Is.EqualTo(0.25f).Within(1e-6f));
		Assert.That(output[1], Is.EqualTo(0.25f).Within(1e-6f));
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
		SampleSound first = Sample(
			new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
			44100,
			NewNotePolicy.Fade(TimeSpan.FromMilliseconds(1)));
		SampleSound second = Sample(
			new float[] { 10.0f, 9.0f, 8.0f, 7.0f },
			44100);
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
		=> FrameTime.FrameStartTime(frame, sampleRate);

	private static SampleSound Sample(
		float[] values,
		int sampleRate,
		NewNotePolicy? newNotePolicy = null)
		=> new(
			Definition(),
			new MemorySampleData(sampleRate, 1, values),
			newNotePolicy);

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

	private sealed class ReleasingTestSound : ISound
	{
		private readonly int _releaseFrames;

		public ReleasingTestSound(
			NewNotePolicy newNotePolicy,
			int releaseFrames)
		{
			NewNotePolicy = newNotePolicy;
			_releaseFrames = releaseFrames;
		}

		public NewNotePolicy NewNotePolicy { get; set; }

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(NewNotePolicy);

		public SoundState CreateState()
			=> new TestSoundState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
		{
			if (!state.NoteOffTime.HasValue)
				return null;

			long offFrame = FrameTime.Ceiling(
				state.NoteOffTime.Value,
				context.Configuration.SampleRate);
			return checked(offFrame + _releaseFrames);
		}

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			long? end = GetEndFrameExclusive(context, state);
			int outputChannels = context.Configuration.OutputChannelCount;

			for (int frame = 0; frame < frameCount; frame++)
			{
				long current = startFrame + frame;
				if (end.HasValue && current >= end.Value)
					break;

				for (int channel = 0; channel < outputChannels; channel++)
					destination[frame * outputChannels + channel] += 1.0f;
			}
		}
	}

	private sealed class TestSoundState : SoundState
	{
	}
	[Test]
	public void EndInputCutRemovesOnlyVoicesThatRemainIndefinite()
	{
		ObjectId finiteId = (ObjectId)20U;
		ObjectId infiniteId = (ObjectId)21U;
		ReleasingTestSound finite =
			new(
				NewNotePolicy.Cut,
				releaseFrames: 2);
		InfiniteTestSound infinite = new();

		PlaybackSession session =
			Session(
				10,
				Schedule(
					Event(
						TimeSpan.Zero,
						0,
						new StartNoteCommand(finiteId)),
					Event(
						TimeSpan.Zero,
						1,
						new StartNoteCommand(infiniteId))),
				new TestResolver(
					(finiteId, false, finite),
					(infiniteId, false, infinite)));

		session.Render(
			0,
			1,
			new float[1]);
		session.EndInput();

		Assert.That(
			session.HasIndefiniteActiveVoices,
			Is.True);

		session.CutIndefiniteActiveVoicesAfterEndInput();

		Assert.That(
			session.HasIndefiniteActiveVoices,
			Is.False);
		Assert.That(
			session.GetChannelState(0).CurrentVoice,
			Is.Not.Null);
		Assert.That(
			session.GetChannelState(1).CurrentVoice,
			Is.Null);
		Assert.That(
			session.GetChannelState(1).AntiClickTail.IsActive,
			Is.True);
	}

	private sealed class InfiniteTestSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;

		public SoundState CreateState()
			=> new TestSoundState();

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
			destination.Fill(1.0f);
		}
	}

	[Test]
	public void StartNoteVolumeIsAppliedAtomicallyToNewVoice()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = Sample(new float[] { 2.0f }, 1);
		PlaybackSession session = Session(
			1,
			Schedule(
				Event(
					TimeSpan.Zero,
					0,
					new StartNoteCommand(
						sourceId,
						Volume: 0.25))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[1];

		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void UnresolvedStartNoteDoesNotApplyItsDirectVolume()
	{
		ObjectId missingId = (ObjectId)10U;
		ObjectId sourceId = (ObjectId)11U;
		SampleSound sound = Sample(new float[] { 1.0f }, 1);
		PlaybackSession session = Session(
			1,
			Schedule(
				Event(
					Frame(0, 1),
					0,
					new StartNoteCommand(
						missingId,
						Volume: 0.25)),
				Event(
					Frame(1, 1),
					0,
					new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[2];

		session.Render(0, 2, output);

		Assert.That(output[0], Is.EqualTo(0.0f));
		Assert.That(output[1], Is.EqualTo(1.0f).Within(1e-6f));
	}

	[Test]
	public void NoteOffReleaseRemainsVolumeControllableUntilLaterCut()
	{
		ObjectId sourceId = (ObjectId)10U;
		ReleasingTestSound sound = new(
			NewNotePolicy.Cut,
			releaseFrames: 10);
		PlaybackSession session = Session(
			1,
			Schedule(
				Event(
					Frame(0, 1),
					0,
					new StartNoteCommand(sourceId)),
				Event(
					Frame(1, 1),
					0,
					new NoteOffCommand(),
					new SetNoteVolumeCommand(0.5)),
				Event(
					Frame(2, 1),
					0,
					new SetNoteVolumeCommand(0.25)),
				Event(
					Frame(3, 1),
					0,
					new NoteCutCommand())),
			new TestResolver((sourceId, false, sound)));
		float[] output = new float[5];

		session.Render(0, 5, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					1.0f,
					0.5f,
					0.25f,
					0.0f,
					0.0f,
				})
				.Within(1e-6f));
		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
	}

}
