using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerSlidePatternEffectTests
{
	[Test]
	public void DataGridTranslatesTrackerSlidesToRawCommands()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x30));
		cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x05));
		cell.Effects.Add(new TrackerPitchSlideUpPatternEffect(0x07));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyVolumeSlideCommand(0x30)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyPitchSlideDownCommand(0x05)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyPitchSlideUpCommand(0x07)));
	}

	[Test]
	public void VolumeSlideFormsMatchItSemantics()
	{
		Assert.That(
			FirstResolved(new TrackerVolumeSlidePatternEffect(0x30)),
			Is.EqualTo(new SetNoteVolumeSlideCommand(3.0)));
		Assert.That(
			FirstResolved(new TrackerVolumeSlidePatternEffect(0x03)),
			Is.EqualTo(new SetNoteVolumeSlideCommand(-3.0)));
		Assert.That(
			FirstResolved(new TrackerVolumeSlidePatternEffect(0x3F)),
			Is.EqualTo(new AdjustNoteVolumeCommand(3.0)));
		Assert.That(
			FirstResolved(new TrackerVolumeSlidePatternEffect(0xF3)),
			Is.EqualTo(new AdjustNoteVolumeCommand(-3.0)));
	}

	[Test]
	public void PitchSlideFormsMatchItSemantics()
	{
		Assert.That(
			FirstResolved(new TrackerPitchSlideUpPatternEffect(0x05)),
			Is.EqualTo(new SetPitchSlideCommand(20.0)));
		Assert.That(
			FirstResolved(new TrackerPitchSlideDownPatternEffect(0x05)),
			Is.EqualTo(new SetPitchSlideCommand(-20.0)));

		Assert.That(
			FirstResolved(new TrackerPitchSlideUpPatternEffect(0xE2)),
			Is.EqualTo(new AdjustPitchLinearUnitsCommand(2.0)));
		Assert.That(
			FirstResolved(new TrackerPitchSlideDownPatternEffect(0xE2)),
			Is.EqualTo(new AdjustPitchLinearUnitsCommand(-2.0)));

		Assert.That(
			FirstResolved(new TrackerPitchSlideUpPatternEffect(0xF2)),
			Is.EqualTo(new AdjustPitchLinearUnitsCommand(8.0)));
		Assert.That(
			FirstResolved(new TrackerPitchSlideDownPatternEffect(0xF2)),
			Is.EqualTo(new AdjustPitchLinearUnitsCommand(-8.0)));
	}

	[Test]
	public void PitchSlideUpAndDownShareWholeByteMemory()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerPitchSlideDownPatternEffect(0x05)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerPitchSlideUpPatternEffect(0x00)),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new SetPitchSlideCommand(20.0)));
	}

	[Test]
	public void VolumeSlideHasIndependentWholeByteMemory()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerVolumeSlidePatternEffect(0x4F)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerVolumeSlidePatternEffect(0x00)),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new AdjustNoteVolumeCommand(4.0)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedSlideSeedMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x30));
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

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.VolumeSlide,
				out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildSharesMappedSlideMemoryWithParent()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerVolumeSlidePatternEffect(0x30),
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder recalled = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerVolumeSlidePatternEffect(0x00)),
			child,
			recalled,
			out _);

		Assert.That(
			recalled.Freeze()[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeSlideCommand(3.0)));

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TrackerVolumeSlidePatternEffect(0x04)),
			child,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder parentRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerVolumeSlidePatternEffect(0x00),
				channel: 4),
			parent,
			parentRecall,
			out _);

		Assert.That(
			parentRecall.Freeze()[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeSlideCommand(-4.0)));
	}

	private static NoteCommand FirstResolved(PatternEffect effect)
	{
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(effect),
			new SequencingContext(),
			output,
			out _);

		return output.Freeze()[0].Commands[0];
	}

	private static DataPatternDefinition PatternWithEffect(
		PatternEffect effect,
		int channel = 0)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(effect);
		return pattern;
	}
}
