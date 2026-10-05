using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class VibratoVolumeSlidePatternEffectTests
{
	[Test]
	public void DataGridTranslatesKxxToRawCombinationCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new VibratoVolumeSlidePatternEffect(0x04));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyVibratoVolumeSlideCommand(0x04)));
	}

	[Test]
	public void KxxResumesNormalVibratoAndAppliesVolumeSlide()
	{
		SequencingContext context = new();

		Generate(
			new VibratoPatternEffect(0x53),
			context);

		NoteSchedule schedule = Generate(
			new VibratoVolumeSlidePatternEffect(0x04),
			context);

		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetNoteVolumeSlideCommand(-4.0),
					new SetVibratoCommand(5, 3),
				}));
	}

	[Test]
	public void KxxResumesFineVibratoDepthExactly()
	{
		SequencingContext context = new();

		Generate(
			new FineVibratoPatternEffect(0x53),
			context);

		NoteSchedule schedule = Generate(
			new VibratoVolumeSlidePatternEffect(0x04),
			context);

		SetVibratoCommand vibrato =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetVibratoCommand>()
				.Single();

		Assert.That(
			vibrato,
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.Sine,
					DepthScale: 0.25)));
	}

	[Test]
	public void K00SharesDxxVolumeSlideMemory()
	{
		SequencingContext context = new();

		Generate(
			new TrackerVolumeSlidePatternEffect(0x40),
			context);

		NoteSchedule schedule = Generate(
			new VibratoVolumeSlidePatternEffect(0x00),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single()
				.TrackerUnitsPerTick,
			Is.EqualTo(4.0));
	}

	[Test]
	public void KxxUpdatesDxxVolumeSlideMemory()
	{
		SequencingContext context = new();

		Generate(
			new VibratoVolumeSlidePatternEffect(0x04),
			context);

		NoteSchedule schedule = Generate(
			new TrackerVolumeSlidePatternEffect(0x00),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single()
				.TrackerUnitsPerTick,
			Is.EqualTo(-4.0));
	}

	[Test]
	public void KxxUsesPersistentVibratoWaveform()
	{
		SequencingContext context = new();

		DataPatternDefinition setup = Pattern(1);
		PatternCell setupCell = setup.Grid.GetOrCreateCell(0, 0);
		setupCell.Effects.Add(
			new TrackerVibratoWaveformPatternEffect(2));
		setupCell.Effects.Add(
			new VibratoPatternEffect(0x53));

		PatternNoteProcessor.GenerateNotes(
			setup,
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteSchedule schedule = Generate(
			new VibratoVolumeSlidePatternEffect(0x04),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetVibratoCommand>()
				.Single()
				.Waveform,
			Is.EqualTo(TrackerWaveform.Square));
	}

	[Test]
	public void FineVolumeSlideInKxxIsImmediateWhileVibratoRunsForRow()
	{
		SequencingContext context = new();

		Generate(
			new VibratoPatternEffect(0x53),
			context);

		NoteSchedule schedule = Generate(
			new VibratoVolumeSlidePatternEffect(0x2F),
			context);

		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new AdjustCurrentNoteVolumeCommand(2.0),
					new SetVibratoCommand(5, 3),
				}));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearNoteVolumeSlideCommand>(),
			Is.Empty);
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearPitchModulationCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[Test]
	public void FinePatternDelayExtendsKxxVolumeSlideTickSpan()
	{
		SequencingContext context = new();
		Generate(
			new VibratoPatternEffect(0x53),
			context);

		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(
			new VibratoVolumeSlidePatternEffect(0x40));
		cell.Effects.Add(
			new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			out _);

		SetNoteVolumeSlideCommand slide =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single();

		Assert.That(
			slide.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
	}

	[Test]
	public void StartRowDoesNotLetSkippedKxxSeedVolumeSlideMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new VibratoVolumeSlidePatternEffect(0x40));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x00));

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
				.Any(c => c is SetNoteVolumeSlideCommand
					or AdjustCurrentNoteVolumeCommand),
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
