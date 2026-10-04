using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ChannelVolumePatternEffectTests
{
	[TestCase((byte)0)]
	[TestCase((byte)32)]
	[TestCase((byte)64)]
	[TestCase((byte)65)]
	[TestCase(byte.MaxValue)]
	public void DataGridTranslatesMxxToRawCommand(byte parameter)
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerChannelVolumePatternEffect(parameter));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyTrackerChannelVolumeCommand(parameter)));
	}

	[TestCase((byte)0, 0.0)]
	[TestCase((byte)1, 1.0 / 64.0)]
	[TestCase((byte)32, 0.5)]
	[TestCase((byte)64, 1.0)]
	public void ProcessorMapsValidMxxToNormalizedOverallChannelVolume(
		byte parameter,
		double expectedVolume)
	{
		NoteSchedule schedule = Generate(
			new TrackerChannelVolumePatternEffect(parameter));

		SetOverallChannelVolumeCommand command =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetOverallChannelVolumeCommand>()
				.Single();

		Assert.That(
			command.Volume,
			Is.EqualTo(expectedVolume));
	}

	[TestCase((byte)65)]
	[TestCase((byte)127)]
	[TestCase(byte.MaxValue)]
	public void ProcessorIgnoresMxxAbove64(byte parameter)
	{
		NoteSchedule schedule = Generate(
			new TrackerChannelVolumePatternEffect(parameter));

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetOverallChannelVolumeCommand>(),
			Is.Empty);
	}

	[Test]
	public void SameCellNewNotePrecedesChannelVolume()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerChannelVolumePatternEffect(32));

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
				new SetOverallChannelVolumeCommand(0.5)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedChannelVolume()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerChannelVolumePatternEffect(16));
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
				.OfType<SetOverallChannelVolumeCommand>(),
			Is.Empty);
	}

	private static NoteSchedule Generate(PatternEffect effect)
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(effect);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);
		return output.Freeze();
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};
}
