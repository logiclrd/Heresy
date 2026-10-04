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
public sealed class PlaybackNewNoteActionOverrideTests
{
	[Test]
	public void S74OverridesCurrentCutVoiceToContinue()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(NewNotePolicy.Cut);
		InfiniteTestSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Continue)),
				Event(Frame(2), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 3, new float[3]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(session.VirtualVoices[0].Sound, Is.SameAs(first));
		Assert.That(
			session.GetChannelState(0).CurrentVoice?.Sound,
			Is.SameAs(second));
	}

	[Test]
	public void S73OverridesCurrentContinueVoiceToCut()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(NewNotePolicy.Continue);
		InfiniteTestSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Cut)),
				Event(Frame(2), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 3, new float[3]);

		Assert.That(session.VirtualVoices, Is.Empty);
		Assert.That(
			session.GetChannelState(0).CurrentVoice?.Sound,
			Is.SameAs(second));
	}

	[Test]
	public void S75OverridesCurrentVoiceToNoteOff()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(NewNotePolicy.Cut);
		InfiniteTestSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Off)),
				Event(Frame(2), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 3, new float[3]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].SoundState.NoteOffTime,
			Is.EqualTo(FrameTime.FrameStartTime(2, 10)));
	}

	[Test]
	public void S76UsesSourceProvidedNewNoteFadeDuration()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(
			NewNotePolicy.Continue,
			newNoteFadeDuration: TimeSpan.FromMilliseconds(200));
		InfiniteTestSound second = new(
			NewNotePolicy.Cut,
			value: 0.0f);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Fade)),
				Event(Frame(2), 0, new StartNoteCommand(secondId))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		float[] output = new float[5];
		session.Render(0, output.Length, output);

		Assert.That(output[2], Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(output[3], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(output[4], Is.EqualTo(0.0f).Within(1e-6f));
		Assert.That(session.VirtualVoices, Is.Empty);
	}

	[Test]
	public void NewNoteResetsOverrideToItsOwnSourcePolicy()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ObjectId thirdId = (ObjectId)12U;
		InfiniteTestSound first = new(NewNotePolicy.Cut);
		InfiniteTestSound second = new(NewNotePolicy.Cut);
		InfiniteTestSound third = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Continue)),
				Event(Frame(2), 0, new StartNoteCommand(secondId)),
				Event(Frame(3), 0, new StartNoteCommand(thirdId))),
			new TestResolver(
				(firstId, first),
				(secondId, second),
				(thirdId, third)));

		session.Render(0, 4, new float[4]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(session.VirtualVoices[0].Sound, Is.SameAs(first));
		Assert.That(
			session.GetChannelState(0).CurrentVoice?.Sound,
			Is.SameAs(third));
	}

	[Test]
	public void SameEventStartThenOverrideDoesNotChangeDisplacementOfOldVoice()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		InfiniteTestSound first = new(NewNotePolicy.Cut);
		InfiniteTestSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0), 0, new StartNoteCommand(firstId)),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(secondId),
					new SetCurrentVoiceDisplacementActionCommand(
						NoteDisplacementAction.Continue))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 2, new float[2]);

		Assert.That(session.VirtualVoices, Is.Empty);
		Assert.That(
			session.GetChannelState(0).CurrentVoice?.Sound,
			Is.SameAs(second));
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

	private static TimeSpan Frame(long frame)
		=> FrameTime.FrameStartTime(frame, 10);

	private sealed class InfiniteTestSound : ISound
	{
		private readonly NewNotePolicy _newNotePolicy;
		private readonly TimeSpan? _newNoteFadeDuration;
		private readonly float _value;

		public InfiniteTestSound(
			NewNotePolicy newNotePolicy,
			TimeSpan? newNoteFadeDuration = null,
			float value = 1.0f)
		{
			_newNotePolicy = newNotePolicy;
			_newNoteFadeDuration = newNoteFadeDuration;
			_value = value;
		}

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(
				_newNotePolicy,
				newNoteFadeDuration: _newNoteFadeDuration);

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
			for (int frame = 0; frame < frameCount; frame++)
				destination[frame] += _value;
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
