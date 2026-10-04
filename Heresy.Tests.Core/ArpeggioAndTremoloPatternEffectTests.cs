using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ArpeggioAndTremoloPatternEffectTests
{
	[Test]
	public void DataGridTranslatesArpeggioAndTremoloToRawCommands()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new ArpeggioPatternEffect(0x37));
		cell.Effects.Add(new TremoloPatternEffect(0x53));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyArpeggioCommand(0x37)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTremoloCommand(0x53)));
	}

	[Test]
	public void ArpeggioUsesWholeByteMemoryAndClearsAtRowEnd()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new ArpeggioPatternEffect(0x37)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new ArpeggioPatternEffect(0x00)),
			context,
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new SetArpeggioCommand(3, 7)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(duration));
		Assert.That(
			schedule[1].Commands[0],
			Is.TypeOf<ClearArpeggioCommand>());
	}

	[Test]
	public void TremoloZeroNibblesRecallIndependently()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TremoloPatternEffect(0x53)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder speedRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TremoloPatternEffect(0x70)),
			context,
			speedRecall,
			out _);

		Assert.That(
			speedRecall.Freeze()[0].Commands[0],
			Is.EqualTo(new SetTremoloCommand(7, 3)));

		NoteScheduleBuilder depthRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TremoloPatternEffect(0x04)),
			context,
			depthRecall,
			out _);

		Assert.That(
			depthRecall.Freeze()[0].Commands[0],
			Is.EqualTo(new SetTremoloCommand(7, 4)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedArpeggioOrTremoloSeedMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(new ArpeggioPatternEffect(0x37));
		row0.Effects.Add(new TremoloPatternEffect(0x53));

		PatternCell row1 = pattern.Grid.GetOrCreateCell(1, 0);
		row1.Effects.Add(new ArpeggioPatternEffect(0x00));

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
				EffectMemorySlot.Arpeggio,
				out _),
			Is.False);
		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.Tremolo,
				out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildSharesMappedArpeggioMemory()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new ArpeggioPatternEffect(0x47),
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new ArpeggioPatternEffect(0x00)),
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new SetArpeggioCommand(4, 7)));
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
