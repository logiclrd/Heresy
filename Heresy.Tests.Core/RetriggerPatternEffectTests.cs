using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class RetriggerPatternEffectTests
{
	[Test]
	public void DataGridTranslatesQxyToRawRetriggerCommand()
	{
		DataPatternDefinition pattern = PatternWithRows(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new RetriggerPatternEffect(0x93));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyRetriggerCommand(0x93)));
	}

	[Test]
	public void NewNoteQ03RetriggersOnTickThree()
	{
		DataPatternDefinition pattern = PatternWithRows(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new RetriggerPatternEffect(0x03));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(schedule[0].Commands[0], Is.TypeOf<StartNoteCommand>());
		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			schedule[1].Commands[0],
			Is.EqualTo(new RetriggerCurrentVoiceCommand(0)));
	}

	[Test]
	public void RetriggerCountdownCarriesAcrossQRows()
	{
		DataPatternDefinition pattern = PatternWithRows(2);

		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Note = new StartPatternNote((ObjectId)10U);
		row0.Effects.Add(new RetriggerPatternEffect(0x03));

		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x00));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			schedule[2].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(
			schedule[3].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));
	}

	[Test]
	public void ZeroIntervalRetriggersEveryActiveTick()
	{
		DataPatternDefinition pattern = PatternWithRows(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new RetriggerPatternEffect(0x90));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();

		Assert.That(schedule.Count, Is.EqualTo(6));
		for (int i = 1; i < schedule.Count; i++)
		{
			Assert.That(
				schedule[i].Offset.TimeOffset,
				Is.EqualTo(TimeSpan.FromMilliseconds(20 * i)));
			Assert.That(
				schedule[i].Commands[0],
				Is.EqualTo(new RetriggerCurrentVoiceCommand(9)));
		}
	}

	[Test]
	public void Q00UsesWholeByteMemory()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(0xB3),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(0x00),
			context,
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new RetriggerCurrentVoiceCommand(0x0B)));
	}

	[Test]
	public void StartRowDoesNotLetSkippedQxySeedMemory()
	{
		DataPatternDefinition pattern = PatternWithRows(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new RetriggerPatternEffect(0x93));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.Retrigger,
				out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildDoesNotRecallParentRetriggerMemoryAndCountdown()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				0xA3,
				channel: 4,
				startNote: true),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(0x00),
			child,
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.Zero));
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new RetriggerCurrentVoiceCommand(0x00)));
	}

	private static DataPatternDefinition PatternWithRows(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};

	private static DataPatternDefinition PatternWithEffect(
		byte parameter,
		int channel = 0,
		bool startNote = false)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, channel);
		if (startNote)
			cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new RetriggerPatternEffect(parameter));
		return pattern;
	}
}
