using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class Panning8BitPatternEffectTests
{
	[TestCase((byte)0)]
	[TestCase((byte)128)]
	[TestCase((byte)255)]
	public void DataGridTranslatesXxxToRawCommand(byte parameter)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanning8BitPatternEffect(parameter));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyTrackerPanning8BitCommand(parameter)));
	}

	[TestCase((byte)0, -1.0f)]
	[TestCase((byte)64, -0.5f)]
	[TestCase((byte)128, 0.0f)]
	[TestCase((byte)255, 0.9921875f)]
	public void ProcessorMapsXxxThroughITPanScale(
		byte parameter,
		float expectedX)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanning8BitPatternEffect(parameter));
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
	public void XFFRemainsOneTrackerPanUnitShortOfS8FFullRight()
	{
		DataPatternDefinition pattern = Pattern(1, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanning8BitPatternEffect(0xFF));
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPanningPatternEffect(0x0F));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Vector3 xff = schedule
			.Single(e => e.Target == ChannelTarget.Physical(0))
			.Commands
			.OfType<SetSpatialPositionCommand>()
			.Single()
			.Position;
		Vector3 s8f = schedule
			.Single(e => e.Target == ChannelTarget.Physical(1))
			.Commands
			.OfType<SetSpatialPositionCommand>()
			.Single()
			.Position;

		Assert.That(xff.X, Is.EqualTo(127.0f / 128.0f));
		Assert.That(s8f.X, Is.EqualTo(1.0f));
	}

	[Test]
	public void SameCellNewNotePrecedesXxxPanning()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerPanning8BitPatternEffect(0x80));

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
				new SetSpatialPositionCommand(Vector3.Zero)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedXxxPanning()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanning8BitPatternEffect(0xFF));
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

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
