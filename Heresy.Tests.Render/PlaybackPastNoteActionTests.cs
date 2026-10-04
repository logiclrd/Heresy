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
public sealed class PlaybackPastNoteActionTests
{
	[Test]
	public void S70CutsAllPastVoicesFromCurrentHostChannelOnly()
	{
		ObjectId a = (ObjectId)10U;
		ObjectId b = (ObjectId)11U;
		ObjectId c = (ObjectId)12U;
		ObjectId other = (ObjectId)13U;

		InfiniteTestSound continueSound = new(
			NewNotePolicy.Continue);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(Frame(0, 10), 0, new StartNoteCommand(a)),
				Event(Frame(1, 10), 0, new StartNoteCommand(b)),
				Event(Frame(2, 10), 0, new StartNoteCommand(c)),
				Event(Frame(0, 10), 1, new StartNoteCommand(other)),
				Event(Frame(1, 10), 1, new StartNoteCommand(a)),
				Event(
					Frame(3, 10),
					0,
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Cut))),
			new TestResolver(
				(a, false, continueSound),
				(b, false, continueSound),
				(c, false, continueSound),
				(other, false, continueSound)));

		session.Render(0, 4, new float[4]);

		Assert.That(
			session.VirtualVoices.Count,
			Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].OriginPhysicalChannel,
			Is.EqualTo(1));
		Assert.That(
			session.GetChannelState(0).CurrentVoice,
			Is.Not.Null);
		Assert.That(
			session.GetChannelState(1).CurrentVoice,
			Is.Not.Null);
	}

	[Test]
	public void SameEventNewNoteThenS70CutsDisplacedVoiceNotNewVoice()
	{
		ObjectId oldId = (ObjectId)10U;
		ObjectId newId = (ObjectId)11U;
		InfiniteTestSound oldSound = new(
			NewNotePolicy.Continue);
		InfiniteTestSound newSound = new(
			NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(
					Frame(0, 10),
					0,
					new StartNoteCommand(oldId)),
				Event(
					Frame(1, 10),
					0,
					new StartNoteCommand(newId),
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Cut))),
			new TestResolver(
				(oldId, false, oldSound),
				(newId, false, newSound)));

		session.Render(0, 2, new float[2]);

		Assert.That(session.VirtualVoices, Is.Empty);
		Assert.That(
			session.GetChannelState(0).CurrentVoice?.Sound,
			Is.SameAs(newSound));
	}

	[Test]
	public void S71AppliesNoteOffToMatchingPastVoicesOnly()
	{
		ObjectId oldId = (ObjectId)10U;
		ObjectId newId = (ObjectId)11U;
		InfiniteTestSound oldSound = new(
			NewNotePolicy.Continue);
		InfiniteTestSound newSound = new(
			NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(
					Frame(0, 10),
					0,
					new StartNoteCommand(oldId)),
				Event(
					Frame(1, 10),
					0,
					new StartNoteCommand(newId)),
				Event(
					Frame(3, 10),
					0,
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Off))),
			new TestResolver(
				(oldId, false, oldSound),
				(newId, false, newSound)));

		session.Render(0, 4, new float[4]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].SoundState.NoteOffTime,
			Is.EqualTo(FrameTime.FrameStartTime(3, 10)));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.SoundState.NoteOffTime,
			Is.Null);
	}

	[Test]
	public void S72RequestsFadeAndUsesIndependentNoteFadeDuration()
	{
		ObjectId oldId = (ObjectId)10U;
		ObjectId newId = (ObjectId)11U;
		InfiniteTestSound oldSound = new(
			NewNotePolicy.Continue,
			noteFadeDuration:
				TimeSpan.FromMilliseconds(200));
		InfiniteTestSound newSound = new(
			NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(
					Frame(0, 10),
					0,
					new StartNoteCommand(oldId)),
				Event(
					Frame(1, 10),
					0,
					new StartNoteCommand(newId)),
				Event(
					Frame(3, 10),
					0,
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Fade))),
			new TestResolver(
				(oldId, false, oldSound),
				(newId, false, newSound)));

		float[] output = new float[6];
		session.Render(0, output.Length, output);

		Assert.That(output[3], Is.EqualTo(2.0f).Within(1e-6f));
		Assert.That(output[4], Is.EqualTo(1.5f).Within(1e-6f));
		Assert.That(output[5], Is.EqualTo(1.0f).Within(1e-6f));

		Assert.That(
			session.GetChannelState(0).CurrentVoice!
				.IsNoteFadeRequested,
			Is.False);
		Assert.That(session.VirtualVoices, Is.Empty);
	}

	[Test]
	public void S72StillRecordsFadeRequestWhenVoiceHasNoFadeEnvelope()
	{
		ObjectId oldId = (ObjectId)10U;
		ObjectId newId = (ObjectId)11U;
		InfiniteTestSound oldSound = new(
			NewNotePolicy.Continue);
		InfiniteTestSound newSound = new(
			NewNotePolicy.Cut);

		PlaybackSession session = Session(
			10,
			Schedule(
				Event(
					Frame(0, 10),
					0,
					new StartNoteCommand(oldId)),
				Event(
					Frame(1, 10),
					0,
					new StartNoteCommand(newId)),
				Event(
					Frame(2, 10),
					0,
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Fade))),
			new TestResolver(
				(oldId, false, oldSound),
				(newId, false, newSound)));

		session.Render(0, 3, new float[3]);

		Assert.That(session.VirtualVoices.Count, Is.EqualTo(1));
		Assert.That(
			session.VirtualVoices[0].IsNoteFadeRequested,
			Is.True);
		Assert.That(
			session.VirtualVoices[0].IsFading,
			Is.False);
	}

	[Test]
	public void CutPastVoiceContributesAntiClickTail()
	{
		ObjectId oldId = (ObjectId)10U;
		ObjectId newId = (ObjectId)11U;
		InfiniteTestSound oldSound = new(
			NewNotePolicy.Continue);
		InfiniteTestSound newSound = new(
			NewNotePolicy.Cut,
			value: 0.0f);

		PlaybackSession session = Session(
			44100,
			Schedule(
				Event(
					Frame(0, 44100),
					0,
					new StartNoteCommand(oldId)),
				Event(
					Frame(2, 44100),
					0,
					new StartNoteCommand(newId)),
				Event(
					Frame(3, 44100),
					0,
					new ApplyPastNoteActionCommand(
						TrackerPastNoteAction.Cut))),
			new TestResolver(
				(oldId, false, oldSound),
				(newId, false, newSound)));

		session.Render(0, 4, new float[4]);

		Assert.That(
			session.GetChannelState(0).AntiClickTail.IsActive,
			Is.True);
	}

	[Test]
	public void NoteFadeDurationMustBePositiveWhenProvided()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new NoteConfigurationSnapshot(
				NewNotePolicy.Continue,
				TimeSpan.Zero));
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

	private static NoteSchedule Schedule(
		params NoteEvent[] events)
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

	private static TimeSpan Frame(
		long frame,
		int sampleRate)
		=> FrameTime.FrameStartTime(frame, sampleRate);

	private sealed class InfiniteTestSound : ISound
	{
		private readonly NewNotePolicy _newNotePolicy;
		private readonly TimeSpan? _noteFadeDuration;
		private readonly float _value;

		public InfiniteTestSound(
			NewNotePolicy newNotePolicy,
			TimeSpan? noteFadeDuration = null,
			float value = 1.0f)
		{
			_newNotePolicy = newNotePolicy;
			_noteFadeDuration = noteFadeDuration;
			_value = value;
		}

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(
				_newNotePolicy,
				_noteFadeDuration);

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
			int outputs =
				context.Configuration.OutputChannelCount;

			for (int frame = 0; frame < frameCount; frame++)
			{
				for (int channel = 0; channel < outputs; channel++)
				{
					destination[frame * outputs + channel] +=
						_value;
				}
			}
		}
	}

	private sealed class TestSoundState : SoundState
	{
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<
			(ObjectId Id, bool Mixdown),
			ISound> _sounds = [];

		public TestResolver(
			params (ObjectId Id, bool Mixdown, ISound Sound)[] sounds)
		{
			foreach (
				(ObjectId id, bool mixdown, ISound sound)
				in sounds)
			{
				_sounds.Add(
					(id, mixdown),
					sound);
			}
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue(
				(sourceId, mixdown),
				out sound);
	}
}
