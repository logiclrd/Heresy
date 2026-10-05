using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerVolumeColumnPanningPatternEffectTests
{
	[TestCase((byte)0, -1.0f)]
	[TestCase((byte)16, -0.5f)]
	[TestCase((byte)32, 0.0f)]
	[TestCase((byte)48, 0.5f)]
	[TestCase((byte)64, 1.0f)]
	public void VolumeColumnPanMapsExactItZeroToSixtyFourScale(
		byte value,
		float expectedX)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(value));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialPositionCommand command =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetSpatialPositionCommand>()
				.Single();

		Assert.That(
			command.Position,
			Is.EqualTo(new Vector3(expectedX, 0.0f, 0.0f)));
	}

	[Test]
	public void VolumeColumnPanRejectsValueAboveSixtyFour()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerVolumeColumnPanningPatternEffect(65));
	}

	[Test]
	public void DataGridPreservesVolumeColumnPanAsTrackerCommand()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(48));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<ApplyTrackerVolumeColumnPanningCommand>()
				.Single(),
			Is.EqualTo(
				new ApplyTrackerVolumeColumnPanningCommand(48)));
	}

	[Test]
	public void SameCellNewNotePrecedesVolumeColumnPanning()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(64));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze().Single().Commands.ToArray();

		Assert.That(commands[0], Is.EqualTo(new StartNoteCommand(sourceId)));
		Assert.That(
			commands[1],
			Is.EqualTo(
				new SetSpatialPositionCommand(
					new Vector3(1.0f, 0.0f, 0.0f))));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void XxxOverridesVolumeColumnPanningRegardlessOfStoredEffectOrder(
		bool volumeFirst)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);

		PatternEffect volumePan =
			new TrackerVolumeColumnPanningPatternEffect(64);
		PatternEffect effectPan =
			new TrackerPanning8BitPatternEffect(0);

		if (volumeFirst)
		{
			cell.Effects.Add(volumePan);
			cell.Effects.Add(effectPan);
		}
		else
		{
			cell.Effects.Add(effectPan);
			cell.Effects.Add(volumePan);
		}

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialPositionCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetSpatialPositionCommand>()
				.ToArray();

		Assert.That(commands, Has.Length.EqualTo(1));
		Assert.That(
			commands[0].Position,
			Is.EqualTo(new Vector3(-1.0f, 0.0f, 0.0f)));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void S8xOverridesVolumeColumnPanningRegardlessOfStoredEffectOrder(
		bool volumeFirst)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);

		PatternEffect volumePan =
			new TrackerVolumeColumnPanningPatternEffect(0);
		PatternEffect effectPan =
			new TrackerPanningPatternEffect(15);

		if (volumeFirst)
		{
			cell.Effects.Add(volumePan);
			cell.Effects.Add(effectPan);
		}
		else
		{
			cell.Effects.Add(effectPan);
			cell.Effects.Add(volumePan);
		}

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialPositionCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetSpatialPositionCommand>()
				.ToArray();

		Assert.That(commands, Has.Length.EqualTo(1));
		Assert.That(
			commands[0].Position,
			Is.EqualTo(new Vector3(1.0f, 0.0f, 0.0f)));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void S91OverridesVolumeColumnPanningRegardlessOfStoredEffectOrder(
		bool volumeFirst)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);

		PatternEffect volumePan =
			new TrackerVolumeColumnPanningPatternEffect(0);
		PatternEffect surround =
			new TrackerSurroundPatternEffect();

		if (volumeFirst)
		{
			cell.Effects.Add(volumePan);
			cell.Effects.Add(surround);
		}
		else
		{
			cell.Effects.Add(surround);
			cell.Effects.Add(volumePan);
		}

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToArray();

		Assert.That(
			commands.OfType<SetSpatialPositionCommand>(),
			Is.Empty);
		Assert.That(
			commands.OfType<SetSurroundCommand>().Single(),
			Is.EqualTo(new SetSurroundCommand(true)));
	}

	[Test]
	public void PanningSlideDoesNotSuppressVolumeColumnPanning()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(32));
		cell.Effects.Add(
			new TrackerPanningSlidePatternEffect(0x01));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToArray();

		Assert.That(
			commands.OfType<SetSpatialPositionCommand>().Single().Position,
			Is.EqualTo(Vector3.Zero));
		Assert.That(
			commands.OfType<SetSpatialXSlideCommand>(),
			Is.Not.Empty);
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedVolumeColumnPanning()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeColumnPanningPatternEffect(64));
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
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetSpatialPositionCommand>(),
			Is.Empty);
	}

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
