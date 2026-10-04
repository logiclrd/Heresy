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
public sealed class PlaybackChannelVolumeSlideTests
{
	[Test]
	public void ChannelVolumeSlideInterpolatesAtOutputFrameResolution()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetOverallChannelVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetOverallChannelVolumeSlideCommand(
						8.0,
						TicksPerRow: 4)),
				Event(
					Frame(8),
					0,
					new ClearOverallChannelVolumeSlideCommand())),
			new TestResolver((sourceId, sound)));

		float[] output = new float[8];
		session.Render(0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					0.5f,
					0.5625f,
					0.625f,
					0.6875f,
					0.75f,
					0.8125f,
					0.875f,
					0.875f,
				}).Within(1e-6f));
	}

	[Test]
	public void ClearingSlideCapturesFinalVolumeForFutureNotes()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ConstantSound first = new(NewNotePolicy.Cut);
		ConstantSound second = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetOverallChannelVolumeCommand(0.5),
					new StartNoteCommand(firstId),
					new SetOverallChannelVolumeSlideCommand(
						8.0,
						TicksPerRow: 4)),
				Event(
					Frame(8),
					0,
					new ClearOverallChannelVolumeSlideCommand(),
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 9, new float[9]);

		Assert.That(
			session.GetChannelState(0).OverallVolume,
			Is.EqualTo(0.875).Within(1e-12));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.OverallVolume,
			Is.EqualTo(0.875).Within(1e-12));
	}

	[Test]
	public void FineAdjustmentClampsToChannelVolumeRange()
	{
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetOverallChannelVolumeCommand(0.9),
					new AdjustOverallChannelVolumeCommand(16.0))),
			new TestResolver());

		session.Render(0, 1, new float[1]);

		Assert.That(
			session.GetChannelState(0).OverallVolume,
			Is.EqualTo(1.0));
	}

	[Test]
	public void SetChannelVolumeCancelsActiveSlide()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetOverallChannelVolumeCommand(0.5),
					new StartNoteCommand(sourceId),
					new SetOverallChannelVolumeSlideCommand(
						8.0,
						TicksPerRow: 4)),
				Event(
					Frame(4),
					0,
					new SetOverallChannelVolumeCommand(0.25))),
			new TestResolver((sourceId, sound)));

		float[] output = new float[8];
		session.Render(0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					0.5f,
					0.5625f,
					0.625f,
					0.6875f,
					0.25f,
					0.25f,
					0.25f,
					0.25f,
				}).Within(1e-6f));
	}

	[Test]
	public void DisplacedVoiceFreezesAtCurrentChannelVolume()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ConstantSound first = new(NewNotePolicy.Continue);
		ConstantSound second = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetOverallChannelVolumeCommand(0.5),
					new StartNoteCommand(firstId),
					new SetOverallChannelVolumeSlideCommand(
						8.0,
						TicksPerRow: 4)),
				Event(
					Frame(4),
					0,
					new StartNoteCommand(secondId)),
				Event(
					Frame(8),
					0,
					new ClearOverallChannelVolumeSlideCommand())),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 9, new float[9]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].OverallVolume,
			Is.EqualTo(0.75).Within(1e-12));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.OverallVolume,
			Is.EqualTo(0.875).Within(1e-12));
	}

	[Test]
	public void SplitCallsProduceSamePcmAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound singleSound = new(NewNotePolicy.Cut);
		ConstantSound splitSound = new(NewNotePolicy.Cut);
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new SetOverallChannelVolumeCommand(0.5),
				new StartNoteCommand(sourceId),
				new SetOverallChannelVolumeSlideCommand(
					8.0,
					TicksPerRow: 4)),
			Event(
				Frame(8),
				0,
				new ClearOverallChannelVolumeSlideCommand()));

		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, singleSound)));
		float[] singleOutput = new float[8];
		single.Render(0, 8, singleOutput);

		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, splitSound)));
		float[] splitOutput = new float[8];
		split.Render(0, 3, splitOutput.AsSpan(0, 3));
		split.Render(3, 2, splitOutput.AsSpan(3, 2));
		split.Render(5, 3, splitOutput.AsSpan(5, 3));

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

		public ConstantSound(NewNotePolicy policy)
			=> _policy = policy;

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
		{
			destination.Fill(1.0f);
		}
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
