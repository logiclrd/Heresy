using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class GlobalVolumeSlidePatternEffectTests
{
	[Test]
	public void DataGridKeepsWxxOnOriginatingPhysicalChannel()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x04));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteEvent noteEvent = output.Freeze().Single();
		Assert.That(
			noteEvent.Target,
			Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(
			noteEvent.Commands[0],
			Is.EqualTo(new ApplyGlobalVolumeSlideCommand(0x04)));
	}

	[TestCase((byte)0x04, -4.0)]
	[TestCase((byte)0x40, 4.0)]
	[TestCase((byte)0x34, 3.0)]
	public void RegularGlobalVolumeSlideUsesITHighNibblePriority(
		byte parameter,
		double expectedTrackerUnitsPerTick)
	{
		NoteSchedule schedule = Generate(
			new TrackerGlobalVolumeSlidePatternEffect(parameter));

		SetGlobalVolumeSlideCommand slide =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeSlideCommand>()
				.Single();

		Assert.That(
			slide.TrackerUnitsPerTick,
			Is.EqualTo(expectedTrackerUnitsPerTick));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearGlobalVolumeSlideCommand>()
				.Count(),
			Is.EqualTo(1));
	}

	[TestCase((byte)0x2F, 2.0)]
	[TestCase((byte)0xF2, -2.0)]
	[TestCase((byte)0xFF, 15.0)]
	public void FineGlobalVolumeSlideResolvesToImmediateAdjustment(
		byte parameter,
		double expectedTrackerUnits)
	{
		NoteSchedule schedule = Generate(
			new TrackerGlobalVolumeSlidePatternEffect(parameter));

		AdjustGlobalVolumeCommand adjust =
			schedule.SelectMany(e => e.Commands)
				.OfType<AdjustGlobalVolumeCommand>()
				.Single();

		Assert.That(
			adjust.TrackerUnits,
			Is.EqualTo(expectedTrackerUnits));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearGlobalVolumeSlideCommand>(),
			Is.Empty);
	}

	[Test]
	public void W00MemoryIsIndependentPerPhysicalChannel()
	{
		DataPatternDefinition pattern = Pattern(2, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x04));
		pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x40));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x00));
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x00));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent[] slideEvents =
			output.Freeze()
				.Where(e => e.Commands
					.OfType<SetGlobalVolumeSlideCommand>()
					.Any())
				.ToArray();

		Assert.That(slideEvents, Has.Length.EqualTo(4));
		Assert.That(
			slideEvents
				.Where(e => e.Target == ChannelTarget.Physical(0))
				.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeSlideCommand>()
				.Select(s => s.TrackerUnitsPerTick),
			Is.EqualTo(new[] { -4.0, -4.0 }));
		Assert.That(
			slideEvents
				.Where(e => e.Target == ChannelTarget.Physical(1))
				.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeSlideCommand>()
				.Select(s => s.TrackerUnitsPerTick),
			Is.EqualTo(new[] { 4.0, 4.0 }));
	}

	[Test]
	public void StartRowDoesNotLetSkippedWxxSeedChannelMemory()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x40));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x00));

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
				.Any(c => c is SetGlobalVolumeSlideCommand
					or AdjustGlobalVolumeCommand),
			Is.False);
	}

	[Test]
	public void FinePatternDelayExtendsContinuousGlobalVolumeSlideTickSpan()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerGlobalVolumeSlidePatternEffect(0x40));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetGlobalVolumeSlideCommand slide =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetGlobalVolumeSlideCommand>()
				.Single();

		Assert.That(
			slide.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
	}

	private static NoteSchedule Generate(PatternEffect effect)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(effect);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);
		return output.Freeze();
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
