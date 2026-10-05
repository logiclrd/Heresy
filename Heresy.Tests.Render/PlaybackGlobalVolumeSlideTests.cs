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
public sealed class PlaybackGlobalVolumeSlideTests
{
	[Test]
	public void GlobalVolumeSlideUsesWholeRowOperatorDelta()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.5)),
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetGlobalVolumeSlideCommand(
						16.0,
						TicksPerRow: 4)),
				Event(
					Frame(8),
					0,
					new ClearGlobalVolumeSlideCommand())),
			new TestResolver((sourceId, sound)));

		float[] output = new float[8];
		session.Render(0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					0.5f,
					0.546875f,
					0.59375f,
					0.640625f,
					0.6875f,
					0.734375f,
					0.78125f,
					0.828125f,
				}).Within(1e-6f));
	}

	[Test]
	public void FineAdjustmentClampsToGlobalVolumeRange()
	{
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.95)),
				Event(
					Frame(0),
					0,
					new AdjustGlobalVolumeCommand(16.0))),
			new TestResolver());

		session.Render(0, 1, new float[1]);

		Assert.That(session.GlobalVolume, Is.EqualTo(1.0));
	}

	[Test]
	public void VxxRebasesButDoesNotCancelActiveWxx()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.5)),
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetGlobalVolumeSlideCommand(
						16.0,
						TicksPerRow: 4)),
				Global(
					Frame(2),
					new SetGlobalVolumeCommand(0.25)),
				Event(
					Frame(8),
					0,
					new ClearGlobalVolumeSlideCommand())),
			new TestResolver((sourceId, sound)));

		float[] output = new float[5];
		session.Render(0, 5, output);

		Assert.That(
			output,
			Is.EqualTo(
				new float[]
				{
					0.5f,
					0.546875f,
					0.34375f,
					0.390625f,
					0.4375f,
				}).Within(1e-6f));
	}

	[Test]
	public void SimultaneousSlideDeltasComposeBeforeEffectiveClamp()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new();
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.0)),
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetGlobalVolumeSlideCommand(
						-1.0,
						TicksPerRow: 2)),
				Event(
					Frame(0),
					1,
					new SetGlobalVolumeSlideCommand(
						1.0,
						TicksPerRow: 2)),
				Event(
					Frame(4),
					0,
					new ClearGlobalVolumeSlideCommand()),
				Event(
					Frame(4),
					1,
					new ClearGlobalVolumeSlideCommand())),
			new TestResolver((sourceId, sound)));

		float[] output = new float[3];
		session.Render(0, 3, output);

		Assert.That(output[0], Is.EqualTo(0.0f).Within(1e-6f));
		Assert.That(output[1], Is.EqualTo(0.0f).Within(1e-6f));
		Assert.That(output[2], Is.EqualTo(0.0f).Within(1e-6f));
	}

	[Test]
	public void VxxAndFineWxxRespectPhysicalChannelOrder()
	{
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.5)),
				Event(
					Frame(1),
					0,
					new AdjustGlobalVolumeCommand(16.0)),
				Event(
					Frame(1),
					1,
					new SetGlobalVolumeCommand(0.25))),
			new TestResolver());

		session.Render(0, 2, new float[2]);

		// Channel 0 raises the global volume first; channel 1's Vxx-style set
		// then wins because tracker channels execute in physical-channel order.
		Assert.That(session.GlobalVolume, Is.EqualTo(0.25));
	}

	[Test]
	public void SplitCallsProduceSamePcmAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new();
		NoteSchedule schedule = Schedule(
			Global(
				Frame(0),
				new SetGlobalVolumeCommand(0.5)),
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId),
				new SetGlobalVolumeSlideCommand(
					16.0,
					TicksPerRow: 4)),
			Event(
				Frame(8),
				0,
				new ClearGlobalVolumeSlideCommand()));

		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		float[] singleOutput = new float[8];
		single.Render(0, 8, singleOutput);

		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, sound)));
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

	private static NoteEvent Global(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Global,
			commands);

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 100);

	private sealed class ConstantSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(NewNotePolicy.Cut);

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
