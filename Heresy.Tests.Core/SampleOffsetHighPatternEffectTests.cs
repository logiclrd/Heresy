using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SampleOffsetHighPatternEffectTests
{
	[Test]
	public void DataGridTranslatesSAxToRawHighOffsetCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetHighPatternEffect(3));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplySampleOffsetHighCommand(3)));
	}

	[Test]
	public void SAxUpdatesPersistentHighOffsetWithoutEmittingPlaybackCommand()
	{
		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(3),
			context,
			output,
			out _);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			context.GetPhysicalChannelState(0).SampleOffsetHigh,
			Is.EqualTo(3));
	}

	[Test]
	public void SAxCombinesWithLaterOxxOnNote()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(3),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x04, startNote: true),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x30400)));
	}

	[Test]
	public void SA0ExplicitlyClearsHighOffset()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(3),
			context,
			new NoteScheduleBuilder(),
			out _);
		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(0),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x04, startNote: true),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x0400)));
	}

	[Test]
	public void HighOffsetCombinesWithRememberedO00LowByte()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x21, startNote: false),
			context,
			new NoteScheduleBuilder(),
			out _);
		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(2),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(0x00, startNote: true),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x22100)));
	}

	[Test]
	public void SAxOnNoteWithoutOxxDoesNotSeek()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SampleOffsetHighPatternEffect(3));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands.Count, Is.EqualTo(1));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
		Assert.That(
			context.GetPhysicalChannelState(0).SampleOffsetHigh,
			Is.EqualTo(3));
	}

	[Test]
	public void StartRowDoesNotLetSkippedSAxChangeHighOffset()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetHighPatternEffect(3));

		PatternCell row1 = pattern.Grid.GetOrCreateCell(1, 0);
		row1.Note = new StartPatternNote((ObjectId)10U);
		row1.Effects.Add(new SampleOffsetPatternEffect(0x04));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x0400)));
		Assert.That(
			context.GetPhysicalChannelState(0).SampleOffsetHigh,
			Is.EqualTo(0));
	}

	[Test]
	public void FlattenedChildDoesNotRecallParentHighOffsetState()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithHighOffset(5, channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithOffset(
				0x07,
				startNote: true),
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[1],
			Is.EqualTo(new SetSourceFrameOffsetCommand(0x00700)));
	}

	[Test]
	public void HighOffsetNibbleMustBeInTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new SampleOffsetHighPatternEffect(16));
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};

	private static DataPatternDefinition PatternWithHighOffset(
		byte highOffset,
		int channel = 0)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(
			new SampleOffsetHighPatternEffect(highOffset));
		return pattern;
	}

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
