using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SurroundCompatibilityTests
{
	[Test]
	public void S91ResolvesToPersistentSurroundRouting()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerSurroundPatternEffect());

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetSurroundCommand>()
				.Single(),
			Is.EqualTo(new SetSurroundCommand(true)));
	}

	[Test]
	public void SameCellNoteStartsBeforeS91TakesEffect()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(new TrackerSurroundPatternEffect());

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands = output.Freeze()[0].Commands.ToArray();
		Assert.That(commands[0], Is.EqualTo(new StartNoteCommand(sourceId)));
		Assert.That(commands[1], Is.EqualTo(new SetSurroundCommand(true)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedS91()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerSurroundPatternEffect());
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetSurroundCommand>(),
			Is.Empty);
	}

	[Test]
	public void SurroundPersistsOnChannelAndFutureNotes()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantStereoSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new SetSurroundCommand(true)),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(sourceId))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[4]);

		Assert.That(session.GetChannelState(0).Surround, Is.True);
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.Surround,
			Is.True);
	}

	[Test]
	public void AbsolutePanningClearsSurround()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantStereoSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetSurroundCommand(true)),
				Event(
					Frame(1),
					0,
					new SetSpatialPositionCommand(
						new Vector3(0.5f, 0.0f, 0.0f)))),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[4]);

		Assert.That(session.GetChannelState(0).Surround, Is.False);
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.Surround,
			Is.False);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void PanningSlideIsIgnoredWhileSurroundIsActive(
		bool fine)
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantStereoSound sound = new(NewNotePolicy.Cut);
		NoteCommand slide = fine
			? new AdjustSpatialXCommand(
				0.5,
				MinimumX: -1.0,
				MaximumX: 1.0)
			: new SetSpatialXSlideCommand(
				0.25,
				TicksPerRow: 4,
				MinimumX: -1.0,
				MaximumX: 1.0);

		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetSurroundCommand(true),
					slide)),
			new TestResolver((sourceId, sound)));

		session.Render(0, 2, new float[4]);

		Assert.That(session.GetChannelState(0).Surround, Is.True);
		Assert.That(
			session.GetChannelState(0).Position,
			Is.EqualTo(Vector3.Zero));
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.Surround,
			Is.True);
	}

	[Test]
	public void DisplacedVoiceKeepsSurroundWhenPhysicalChannelReturnsToPanning()
	{
		ObjectId firstId = (ObjectId)10U;
		ObjectId secondId = (ObjectId)11U;
		ConstantStereoSound first = new(NewNotePolicy.Continue);
		ConstantStereoSound second = new(NewNotePolicy.Cut);

		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(firstId),
					new SetSurroundCommand(true)),
				Event(
					Frame(1),
					0,
					new StartNoteCommand(secondId),
					new SetSpatialPositionCommand(
						new Vector3(0.5f, 0.0f, 0.0f)))),
			new TestResolver(
				(firstId, first),
				(secondId, second)));

		session.Render(0, 2, new float[4]);

		Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
		Assert.That(session.VirtualVoices[0].Surround, Is.True);
		Assert.That(
			session.GetChannelState(0).CurrentVoice!.Surround,
			Is.False);
	}

	[Test]
	public void StereoSurroundUsesImpulseTrackerPhaseEncoding()
	{
		ObjectId sourceId = (ObjectId)10U;
		ConstantStereoSound sound = new(NewNotePolicy.Cut);
		PlaybackSession session = Session(
			Schedule(
				Event(
					Frame(0),
					0,
					new StartNoteCommand(sourceId),
					new SetSurroundCommand(true))),
			new TestResolver((sourceId, sound)));

		float[] output = new float[2];
		session.Render(0, 1, output);

		Assert.That(output, Is.EqualTo(new[] { 1.0f, -1.0f }));
	}

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};

	private static PlaybackSession Session(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(RenderConfiguration.Stereo(10)),
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

	private sealed class ConstantStereoSound : ISound
	{
		private readonly NewNotePolicy _policy;

		public ConstantStereoSound(NewNotePolicy policy)
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
			for (int frame = 0; frame < frameCount; frame++)
			{
				destination[frame * 2] += 1.0f;
				destination[frame * 2 + 1] += 1.0f;
			}
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
