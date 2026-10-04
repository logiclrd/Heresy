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
public sealed class PlaybackGlobalVolumeTests
{
	[Test]
	public void GlobalVolumeScalesCompleteMixedOutput()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ConstantSound first = new(1.0f);
		ConstantSound second = new(2.0f);
		PlaybackSession session = Session(
			Schedule(
				Global(
					Frame(0),
					new SetGlobalVolumeCommand(0.5)),
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId)),
				Event(
					Frame(0),
					1,
					new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		float[] output = new float[2];
		session.Render(0, 2, output);

		Assert.That(
			output,
			Is.EqualTo(new[] { 1.5f, 1.5f }).Within(1e-6f));
		Assert.That(session.GlobalVolume, Is.EqualTo(0.5));
	}

	[Test]
	public void GlobalVolumeChangeAffectsExistingAndVirtualVoices()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ConstantSound first = new(1.0f, NewNotePolicy.Continue);
		ConstantSound second = new(1.0f);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(secondId)),
				Global(
					Frame(2),
					new SetGlobalVolumeCommand(0.25))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(
			output,
			Is.EqualTo(
				new[]
				{
					1.0f,
					2.0f,
					0.5f,
					0.5f,
				}).Within(1e-6f));
		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
	}

	[Test]
	public void SplitCallsProduceSamePcmAsSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantSound sound = new(1.0f);
		NoteSchedule schedule = Schedule(
			Event(
				Frame(0),
				0,
				new StartNoteCommand(sourceId)),
			Global(
				Frame(3),
				new SetGlobalVolumeCommand(0.5)));

		PlaybackSession single = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		float[] singleOutput = new float[6];
		single.Render(0, 6, singleOutput);

		PlaybackSession split = Session(
			schedule,
			new TestResolver((sourceId, sound)));
		float[] splitOutput = new float[6];
		split.Render(0, 2, splitOutput.AsSpan(0, 2));
		split.Render(2, 2, splitOutput.AsSpan(2, 2));
		split.Render(4, 2, splitOutput.AsSpan(4, 2));

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
		private readonly float _value;
		private readonly NewNotePolicy _policy;

		public ConstantSound(
			float value,
			NewNotePolicy? policy = null)
		{
			_value = value;
			_policy = policy ?? NewNotePolicy.Cut;
		}

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
			destination.Fill(_value);
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
