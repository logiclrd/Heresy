using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackTremorTests
{
	[Test]
	public void I32GatesThreeTicksOnTwoTicksOffAtTrackerBoundaries()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetTremorCommand(
						3,
						2,
						TicksPerRow: 6)),
				Event(
					Frame(12),
					0,
					new ClearTremorCommand())),
			new TestResolver(
				(sourceId, new ConstantSound())));

		float[] output = new float[12];
		session.Render(0, 12, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					1, 1,
					1, 1,
					1, 1,
					0, 0,
					0, 0,
					1, 1,
				}));
	}

	[Test]
	public void I00WithoutPriorPhaseAlternatesOneTickOnOneTickOff()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetTremorCommand(
						1,
						1,
						TicksPerRow: 4)),
				Event(
					Frame(8),
					0,
					new ClearTremorCommand())),
			new TestResolver(
				(sourceId, new ConstantSound())));

		float[] output = new float[8];
		session.Render(0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					1, 1,
					0, 0,
					1, 1,
					0, 0,
				}));
	}

	[Test]
	public void TremorPhaseFreezesWhileEffectIsInactiveAndThenResumes()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetTremorCommand(
						3,
						2,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearTremorCommand()),
				Event(
					Frame(8),
					0,
					new SetTremorCommand(
						3,
						2,
						TicksPerRow: 2)),
				Event(
					Frame(12),
					0,
					new ClearTremorCommand())),
			new TestResolver(
				(sourceId, new ConstantSound())));

		float[] output = new float[12];
		session.Render(0, 12, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					1, 1,
					1, 1,
					1, 1,
					1, 1,
					1, 1,
					0, 0,
				}));
	}

	[Test]
	public void NnaVoiceStopsFollowingPhysicalChannelTremor()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId),
					new SetTremorCommand(
						1,
						1,
						TicksPerRow: 4)),
				Event(
					Frame(2),
					0,
					new StartNoteCommand(secondId)),
				Event(
					Frame(8),
					0,
					new ClearTremorCommand())),
			new TestResolver(
				(firstId, new ConstantSound(NewNotePolicy.Continue)),
				(secondId, new ConstantSound())));

		float[] output = new float[6];
		session.Render(0, 6, output);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					1, 1,
					1, 1,
					2, 2,
				}));
	}

	[Test]
	public void TremorStateDoesNotAdvanceWithoutCurrentVoice()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetTremorCommand(
						1,
						1,
						TicksPerRow: 4)),
				Event(
					Frame(4),
					0,
					new StartNoteCommand(sourceId)),
				Event(
					Frame(8),
					0,
					new ClearTremorCommand())),
			new TestResolver(
				(sourceId, new ConstantSound())));

		float[] output = new float[8];
		session.Render(0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					0, 0,
					0, 0,
					1, 1,
					0, 0,
				}));
	}

	[Test]
	public void SplitCallsProduceSamePcmAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetTremorCommand(
					3,
					2,
					TicksPerRow: 6)),
			Event(
				Frame(12),
				0,
				new ClearTremorCommand()));

		PlaybackSession single = Session(
			schedule,
			new TestResolver(
				(sourceId, new ConstantSound())));
		float[] singleOutput = new float[12];
		single.Render(0, 12, singleOutput);

		PlaybackSession split = Session(
			schedule,
			new TestResolver(
				(sourceId, new ConstantSound())));
		float[] splitOutput = new float[12];
		split.Render(0, 3, splitOutput.AsSpan(0, 3));
		split.Render(3, 4, splitOutput.AsSpan(3, 4));
		split.Render(7, 5, splitOutput.AsSpan(7, 5));

		Assert.That(splitOutput, Is.EqualTo(singleOutput));
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
		TimeSpan time,
		int channel,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(channel),
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private sealed class ConstantSound : ISound
	{
		private readonly NewNotePolicy _policy;

		public ConstantSound(NewNotePolicy? policy = null)
			=> _policy = policy ?? NewNotePolicy.Cut;

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(_policy);

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
