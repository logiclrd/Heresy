using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PanningSlidePatternEffectTests
{
	[Test]
	public void DataGridTranslatesPxxToRawCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningSlidePatternEffect(0x04));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyPanningSlideCommand(0x04)));
	}

	[TestCase((byte)0x04, 0.125)]
	[TestCase((byte)0x40, -0.125)]
	public void RegularPanningSlideResolvesToContinuousSpatialXSlide(
		byte parameter,
		double expectedUnitsPerTick)
	{
		NoteSchedule schedule = Generate(
			new TrackerPanningSlidePatternEffect(parameter));

		SetSpatialXSlideCommand slide =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetSpatialXSlideCommand>()
				.Single();

		Assert.That(
			slide.SpatialUnitsPerTick,
			Is.EqualTo(expectedUnitsPerTick));
		Assert.That(slide.MinimumX, Is.EqualTo(-1.0));
		Assert.That(slide.MaximumX, Is.EqualTo(1.0));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearSpatialXSlideCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[TestCase((byte)0x2F, -0.0625)]
	[TestCase((byte)0xF2, 0.0625)]
	[TestCase((byte)0xFF, -0.46875)]
	public void FinePanningSlideResolvesToImmediateBoundedAdjustment(
		byte parameter,
		double expectedDelta)
	{
		NoteSchedule schedule = Generate(
			new TrackerPanningSlidePatternEffect(parameter));

		AdjustSpatialXCommand adjust =
			schedule.SelectMany(e => e.Commands)
				.OfType<AdjustSpatialXCommand>()
				.Single();

		Assert.That(adjust.DeltaX, Is.EqualTo(expectedDelta));
		Assert.That(adjust.MinimumX, Is.EqualTo(-1.0));
		Assert.That(adjust.MaximumX, Is.EqualTo(1.0));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearSpatialXSlideCommand>(),
			Is.Empty);
	}

	[Test]
	public void OrdinaryPxyWithBothNibblesSetIsIgnoredLikeImpulseTracker()
	{
		NoteSchedule schedule = Generate(
			new TrackerPanningSlidePatternEffect(0x34));

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.Any(c => c is SetSpatialXSlideCommand
					or AdjustSpatialXCommand),
			Is.False);
	}

	[Test]
	public void P00RecallsWholeBytePanningSlideMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningSlidePatternEffect(0x04));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPanningSlidePatternEffect(0x00));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialXSlideCommand[] slides =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetSpatialXSlideCommand>()
				.ToArray();

		Assert.That(slides, Has.Length.EqualTo(2));
		Assert.That(
			slides.Select(s => s.SpatialUnitsPerTick),
			Is.EqualTo(new[] { 0.125, 0.125 }));
	}

	[Test]
	public void StartRowDoesNotLetSkippedPanningSlideSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanningSlidePatternEffect(0x04));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPanningSlidePatternEffect(0x00));

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
				.Any(c => c is SetSpatialXSlideCommand
					or AdjustSpatialXCommand),
			Is.False);
	}

	[Test]
	public void FinePatternDelayExtendsContinuousPanningSlideTickSpan()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerPanningSlidePatternEffect(0x04));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetSpatialXSlideCommand slide =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetSpatialXSlideCommand>()
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
