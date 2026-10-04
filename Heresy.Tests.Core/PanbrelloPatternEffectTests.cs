using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PanbrelloPatternEffectTests
{
	[Test]
	public void DataGridTranslatesYxxToRawCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PanbrelloPatternEffect(0x53));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyPanbrelloCommand(0x53)));
	}

	[Test]
	public void ProcessorUsesNibbleWisePanbrelloMemory()
	{
		SequencingContext context = new();

		Generate(new PanbrelloPatternEffect(0x53), context);

		SetPanbrelloCommand speedOnly =
			Generate(new PanbrelloPatternEffect(0x70), context)
				.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single();

		Assert.That(
			speedOnly,
			Is.EqualTo(new SetPanbrelloCommand(7, 3)));

		SetPanbrelloCommand depthOnly =
			Generate(new PanbrelloPatternEffect(0x04), context)
				.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single();

		Assert.That(
			depthOnly,
			Is.EqualTo(new SetPanbrelloCommand(7, 4)));
	}

	[Test]
	public void VirginY00StillActivatesZeroDepthPanbrello()
	{
		NoteSchedule schedule = Generate(
			new PanbrelloPatternEffect(0x00),
			new SequencingContext());

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single(),
			Is.EqualTo(new SetPanbrelloCommand(0, 0)));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearPanbrelloCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[Test]
	public void FinePatternDelayExtendsPanbrelloTickSpan()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new PanbrelloPatternEffect(0x53));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetPanbrelloCommand command =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single();

		Assert.That(
			command.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
	}

	[Test]
	public void PatternDelayReappliesPanbrello()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new PanbrelloPatternEffect(0x53));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Count(),
			Is.EqualTo(2));
	}

	[Test]
	public void StartRowDoesNotLetSkippedYxxSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PanbrelloPatternEffect(0x53));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new PanbrelloPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single(),
			Is.EqualTo(new SetPanbrelloCommand(0, 0)));
		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.Panbrello,
					out _),
			Is.False);
	}

	private static NoteSchedule Generate(
		PatternEffect effect,
		SequencingContext context)
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(effect);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
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
