using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ChannelVolumeSlidePatternEffectTests
{
	[Test]
	public void DataGridTranslatesNxxToRawCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerChannelVolumeSlidePatternEffect(0x04));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyChannelVolumeSlideCommand(0x04)));
	}

	[TestCase((byte)0x04, -4.0)]
	[TestCase((byte)0x40, 4.0)]
	[TestCase((byte)0x34, -4.0)]
	public void RegularChannelVolumeSlideResolvesToContinuousSlide(
		byte parameter,
		double expectedTrackerUnitsPerTick)
	{
		NoteSchedule schedule = Generate(
			new TrackerChannelVolumeSlidePatternEffect(parameter));

		SetOverallChannelVolumeSlideCommand slide =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetOverallChannelVolumeSlideCommand>()
				.Single();

		Assert.That(
			slide.TrackerUnitsPerTick,
			Is.EqualTo(expectedTrackerUnitsPerTick));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearOverallChannelVolumeSlideCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[TestCase((byte)0x2F, 2.0)]
	[TestCase((byte)0xF2, -2.0)]
	[TestCase((byte)0xFF, 15.0)]
	public void FineChannelVolumeSlideResolvesToImmediateAdjustment(
		byte parameter,
		double expectedTrackerUnits)
	{
		NoteSchedule schedule = Generate(
			new TrackerChannelVolumeSlidePatternEffect(parameter));

		AdjustOverallChannelVolumeCommand adjust =
			schedule.SelectMany(e => e.Commands)
				.OfType<AdjustOverallChannelVolumeCommand>()
				.Single();

		Assert.That(
			adjust.TrackerUnits,
			Is.EqualTo(expectedTrackerUnits));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearOverallChannelVolumeSlideCommand>(),
			Is.Empty);
	}

	[Test]
	public void N00RecallsWholeByteChannelVolumeSlideMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerChannelVolumeSlidePatternEffect(0x04));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerChannelVolumeSlidePatternEffect(0x00));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetOverallChannelVolumeSlideCommand[] slides =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetOverallChannelVolumeSlideCommand>()
				.ToArray();

		Assert.That(slides, Has.Length.EqualTo(2));
		Assert.That(
			slides.Select(s => s.TrackerUnitsPerTick),
			Is.EqualTo(new[] { -4.0, -4.0 }));
	}

	[Test]
	public void StartRowDoesNotLetSkippedChannelVolumeSlideSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerChannelVolumeSlidePatternEffect(0x04));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerChannelVolumeSlidePatternEffect(0x00));

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
				.Any(c => c is SetOverallChannelVolumeSlideCommand
					or AdjustOverallChannelVolumeCommand),
			Is.False);
	}

	[Test]
	public void FinePatternDelayExtendsContinuousChannelVolumeSlideTickSpan()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerChannelVolumeSlidePatternEffect(0x40));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetOverallChannelVolumeSlideCommand slide =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetOverallChannelVolumeSlideCommand>()
				.Single();

		Assert.That(
			slide.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
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
