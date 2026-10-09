using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class VibratoPatternEffectTests
{
	[Test]
	public void GridVibratoTranslatesToRawMemoryAwareCommand()
	{
		DataPatternDefinition pattern = PatternWithVibrato(2, 1, 0x53);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(new SequencingContext(), output, out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands[0], Is.EqualTo(new ApplyVibratoCommand(0x53)));
	}

	[Test]
	public void ProcessorResolvesVibratoMemoryAndClearsAtEndOfRow()
	{
		DataPatternDefinition pattern = PatternWithVibrato(1, 0, 0x53);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(schedule[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(schedule[0].Commands[0], Is.EqualTo(new SetVibratoCommand(5, 3)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(duration));
		Assert.That(schedule[1].Commands[0], Is.TypeOf<ClearPitchModulationCommand>());
	}

	[Test]
	public void ConsecutiveRowsClearOldModulationBeforeApplyingNextRow()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(new VibratoPatternEffect(0x53));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(new VibratoPatternEffect(0x27));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(4));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule[2].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule[1].Commands[0], Is.TypeOf<ClearPitchModulationCommand>());
		Assert.That(schedule[2].Commands[0], Is.EqualTo(new SetVibratoCommand(2, 7)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedVibratoChangeEffectMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(new VibratoPatternEffect(0x53));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(new VibratoPatternEffect(0x00));
		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(pattern, context, output, 1, out _);

		NoteSchedule schedule = output.Freeze();
		SetVibratoCommand command = (SetVibratoCommand)schedule[0].Commands[0];

		Assert.That(command, Is.EqualTo(new SetVibratoCommand(0, 0)));
		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(EffectMemorySlot.Vibrato, out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildHasIndependentVibratoMemoryFromParent()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		DataPatternDefinition parentSet = PatternWithVibrato(1, 4, 0x53);
		PatternNoteProcessor.GenerateNotes(
			parentSet,
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child = parent.FlattenedChild(physicalChannelOffset: 4);
		DataPatternDefinition childRecall = PatternWithVibrato(1, 0, 0x00);
		NoteScheduleBuilder recallOutput = new();
		PatternNoteProcessor.GenerateNotes(childRecall, child, recallOutput, out _);
		SetVibratoCommand recalled =
			(SetVibratoCommand)recallOutput.Freeze()[0].Commands[0];

		DataPatternDefinition childSet = PatternWithVibrato(1, 0, 0x27);
		PatternNoteProcessor.GenerateNotes(
			childSet,
			child,
			new NoteScheduleBuilder(),
			out _);

		DataPatternDefinition parentRecall = PatternWithVibrato(1, 4, 0x00);
		NoteScheduleBuilder parentOutput = new();
		PatternNoteProcessor.GenerateNotes(parentRecall, parent, parentOutput, out _);
		SetVibratoCommand recalledByParent =
			(SetVibratoCommand)parentOutput.Freeze()[0].Commands[0];

		Assert.That(recalled, Is.EqualTo(new SetVibratoCommand(0, 0)));
		Assert.That(recalledByParent, Is.EqualTo(new SetVibratoCommand(5, 3)),
			"Child vibrato H27 must not overwrite the parent's H53 memory.");
	}

	[Test]
	public void MixdownChildDoesNotRecallParentsVibratoMemory()
	{
		SequencingContext parent = new();
		DataPatternDefinition parentSet = PatternWithVibrato(1, 0, 0x53);
		PatternNoteProcessor.GenerateNotes(
			parentSet,
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext mixdown = parent.MixdownChild();
		DataPatternDefinition childRecall = PatternWithVibrato(1, 0, 0x00);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(childRecall, mixdown, output, out _);

		SetVibratoCommand recalled =
			(SetVibratoCommand)output.Freeze()[0].Commands[0];
		Assert.That(recalled, Is.EqualTo(new SetVibratoCommand(0, 0)));
	}

	[Test]
	public void ZeroNibblesRecallSpeedAndDepthIndependently()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithVibrato(1, 0, 0x53),
			context,
			new NoteScheduleBuilder(),
			out _);
		PatternNoteProcessor.GenerateNotes(
			PatternWithVibrato(1, 0, 0x70),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithVibrato(1, 0, 0x04),
			context,
			output,
			out _);

		SetVibratoCommand command =
			(SetVibratoCommand)output.Freeze()[0].Commands[0];
		Assert.That(command, Is.EqualTo(new SetVibratoCommand(7, 4)));
	}

	private static DataPatternDefinition PatternWithVibrato(
		int rowCount,
		int channel,
		byte parameter)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = rowCount,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(
			new VibratoPatternEffect(parameter));
		return pattern;
	}
}
