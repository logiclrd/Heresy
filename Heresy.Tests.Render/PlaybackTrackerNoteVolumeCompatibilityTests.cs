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
public sealed class PlaybackTrackerNoteVolumeCompatibilityTests
{
	[Test]
	public void CurrentVoiceFineAdjustmentDoesNotSeedIdleChannelVolume()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new AdjustCurrentNoteVolumeCommand(-32.0)),
				Event(
					Frame(1, 4),
					new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId, new ConstantSound(1.0f))));

		float[] output = new float[2];
		session.Render(0, 2, output);

		Assert.That(output[0], Is.EqualTo(0.0f).Within(1e-6));
		Assert.That(output[1], Is.EqualTo(1.0f).Within(1e-6));
		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(1.0));
	}

	[Test]
	public void CurrentVoiceFineAdjustmentUpdatesActiveVoiceAndCapturedVolume()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(1, 4),
					new AdjustCurrentNoteVolumeCommand(-32.0))),
			new TestResolver(
				(sourceId, new ConstantSound(1.0f))));

		float[] output = new float[2];
		session.Render(0, 2, output);

		Assert.That(
			output,
			Is.EqualTo(new[] { 1.0f, 0.5f }).Within(1e-6));
		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(0.5));
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

	private sealed class ConstantSound : ISound
	{
		private readonly float _value;

		public ConstantSound(float value)
			=> _value = value;

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(NewNotePolicy.Cut);

		public SoundState CreateState()
			=> new ConstantState();

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
			for (int i = 0; i < destination.Length; i++)
				destination[i] += _value;
		}
	}

	private sealed class ConstantState : SoundState
	{
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<ObjectId, ISound> _sounds = [];

		public TestResolver(params (ObjectId Id, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, ISound sound) in sounds)
				_sounds.Add(id, sound);
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue(sourceId, out sound);
	}
}
