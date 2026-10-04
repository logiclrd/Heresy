using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class GlobalVolumePatternEffectTests
{
	[TestCase((byte)0)]
	[TestCase((byte)64)]
	[TestCase((byte)128)]
	[TestCase((byte)129)]
	[TestCase(byte.MaxValue)]
	public void DataGridTranslatesVxxToRawGlobalCommand(byte parameter)
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumePatternEffect(parameter));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteEvent noteEvent = output.Freeze().Single();
		Assert.That(noteEvent.Target, Is.EqualTo(ChannelTarget.Global));
		Assert.That(
			noteEvent.Commands[0],
			Is.EqualTo(new ApplyTrackerGlobalVolumeCommand(parameter)));
	}

	[TestCase((byte)0, 0.0)]
	[TestCase((byte)1, 1.0 / 128.0)]
	[TestCase((byte)64, 0.5)]
	[TestCase((byte)128, 1.0)]
	public void ProcessorMapsValidVxxToNormalizedGlobalVolume(
		byte parameter,
		double expectedVolume)
	{
		NoteSchedule schedule = Generate(
			new TrackerGlobalVolumePatternEffect(parameter));

		SetGlobalVolumeCommand command =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeCommand>()
				.Single();

		Assert.That(command.Volume, Is.EqualTo(expectedVolume));
		Assert.That(
			schedule.Single().Target,
			Is.EqualTo(ChannelTarget.Global));
	}

	[TestCase((byte)129)]
	[TestCase((byte)192)]
	[TestCase(byte.MaxValue)]
	public void ProcessorIgnoresVxxAbove128(byte parameter)
	{
		NoteSchedule schedule = Generate(
			new TrackerGlobalVolumePatternEffect(parameter));

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeCommand>(),
			Is.Empty);
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedGlobalVolume()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumePatternEffect(64));
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
				.OfType<SetGlobalVolumeCommand>(),
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
