using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TremorPatternEffectTests
{
	[Test]
	public void DataGridTranslatesIxxToRawCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TremorPatternEffect(0x32));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyTremorCommand(0x32)));
	}

	[TestCase((byte)0x32, (byte)3, (byte)2)]
	[TestCase((byte)0x20, (byte)2, (byte)1)]
	[TestCase((byte)0x02, (byte)1, (byte)2)]
	[TestCase((byte)0x00, (byte)1, (byte)1)]
	public void ProcessorResolvesModernITTremorDurations(
		byte parameter,
		byte expectedOnTicks,
		byte expectedOffTicks)
	{
		NoteSchedule schedule = Generate(
			new TremorPatternEffect(parameter),
			new SequencingContext());

		SetTremorCommand tremor =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetTremorCommand>()
				.Single();

		Assert.That(tremor.OnTicks, Is.EqualTo(expectedOnTicks));
		Assert.That(tremor.OffTicks, Is.EqualTo(expectedOffTicks));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearTremorCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[Test]
	public void I00RecallsWholeByteMemory()
	{
		SequencingContext context = new();

		Generate(
			new TremorPatternEffect(0x32),
			context);

		NoteSchedule recalled = Generate(
			new TremorPatternEffect(0x00),
			context);

		Assert.That(
			recalled.SelectMany(e => e.Commands)
				.OfType<SetTremorCommand>()
				.Single(),
			Is.EqualTo(new SetTremorCommand(3, 2)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedIxxSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TremorPatternEffect(0x32));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TremorPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		SetTremorCommand tremor =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetTremorCommand>()
				.Single();

		Assert.That(tremor, Is.EqualTo(new SetTremorCommand(1, 1)));
		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.Tremor,
					out _),
			Is.False);
	}

	[Test]
	public void FinePatternDelayExtendsTremorTickSpan()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TremorPatternEffect(0x32));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetTremorCommand tremor =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetTremorCommand>()
				.Single();

		Assert.That(
			tremor.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
	}

	[Test]
	public void PatternDelayReappliesTremorWithoutChangingDurations()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TremorPatternEffect(0x32));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetTremorCommand[] tremors =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetTremorCommand>()
				.ToArray();

		Assert.That(tremors, Has.Length.EqualTo(2));
		Assert.That(
			tremors,
			Is.EqualTo(
				new[]
				{
					new SetTremorCommand(3, 2),
					new SetTremorCommand(3, 2),
				}));
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
