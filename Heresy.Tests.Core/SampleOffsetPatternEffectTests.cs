using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SampleOffsetPatternEffectTests
{
	[Test]
	public void DataGridTranslatesOxxToRawSampleOffsetCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SampleOffsetPatternEffect(0x34));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplySampleOffsetCommand(0x34)));
	}

	[Test]
	public void NoteWithOxxResolvesToExactSourceFrameOffset()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SampleOffsetPatternEffect(0x34));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Commands[0],
			Is.TypeOf<StartNoteCommand>());
		Assert.That(
			schedule[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x3400)));
	}

	[Test]
	public void EffectOnlyOxxUpdatesMemoryWithoutSeekingCurrentVoice()
	{
		SequencingContext context = new();

		NoteScheduleBuilder first = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x21, startNote: false),
			context,
			first,
			out _);

		Assert.That(first.Freeze().Count, Is.EqualTo(0));

		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.SampleOffset,
				out byte remembered),
			Is.True);
		Assert.That(remembered, Is.EqualTo(0x21));
	}

	[Test]
	public void O00RecallsPreviousOffsetWhenNewNoteArrives()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x21, startNote: false),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x00, startNote: true),
			context,
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x2100)));
	}

	[Test]
	public void InitialO00WithNoteMeansSourceFrameZero()
	{
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x00, startNote: true),
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedOxxSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2);

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(new SampleOffsetPatternEffect(0x42));

		PatternCell row1 = pattern.Grid.GetOrCreateCell(1, 0);
		row1.Note = new StartPatternNote((ObjectId)10U);
		row1.Effects.Add(new SampleOffsetPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0)));
		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.SampleOffset,
				out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildDoesNotRecallParentSampleOffsetMemory()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(
				0x17,
				startNote: false,
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(
				0x00,
				startNote: true),
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0)));
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};

	private static DataPatternDefinition PatternWithOffset(
		byte parameter,
		bool startNote,
		int channel = 0)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, channel);
		if (startNote)
			cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SampleOffsetPatternEffect(parameter));

		return pattern;
	}
}
