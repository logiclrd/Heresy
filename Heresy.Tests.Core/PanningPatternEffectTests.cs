using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PanningPatternEffectTests
{
	[TestCase((byte)0)]
	[TestCase((byte)7)]
	[TestCase((byte)8)]
	[TestCase((byte)15)]
	public void DataGridTranslatesS8xToRawCommand(byte value)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningPatternEffect(value));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyTrackerPanningCommand(value)));
	}

	[TestCase((byte)0, -1.0f)]
	[TestCase((byte)7, -0.0625f)]
	[TestCase((byte)8, 0.0703125f)]
	[TestCase((byte)15, 1.0f)]
	public void ProcessorResolvesS8xToSpatialX(
		byte value,
		float expectedX)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningPatternEffect(value));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialPositionCommand command =
			output.Freeze()[0].Commands
				.OfType<SetSpatialPositionCommand>()
				.Single();

		Assert.That(
			command.Position,
			Is.EqualTo(new Vector3(expectedX, 0.0f, 0.0f)));
	}

	[Test]
	public void SameCellNewNotePrecedesPanning()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(new TrackerPanningPatternEffect(15));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent noteEvent = output.Freeze()[0];

		Assert.That(
			noteEvent.Commands[0],
			Is.EqualTo(new StartNoteCommand(sourceId)));
		Assert.That(
			noteEvent.Commands[1],
			Is.EqualTo(
				new SetSpatialPositionCommand(
					new Vector3(1.0f, 0.0f, 0.0f))));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedPanning()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningPatternEffect(0));
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
				.OfType<SetSpatialPositionCommand>(),
			Is.Empty);
	}

	[Test]
	public void PanningRejectsValueAboveLowNibbleRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerPanningPatternEffect(16));
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
