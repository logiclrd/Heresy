using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerWaveformPatternEffectTests
{
	[Test]
	public void DataGridTranslatesS3xAndS4xToRawCommands()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVibratoWaveformPatternEffect(1));
		cell.Effects.Add(new TrackerTremoloWaveformPatternEffect(2));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerVibratoWaveformCommand(1)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new ApplyTrackerTremoloWaveformCommand(2)));
	}

	[Test]
	public void S31PersistsRampDownIntoLaterVibrato()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerVibratoWaveformPatternEffect(1)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new VibratoPatternEffect(0x53)),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.RampDown)));
	}

	[Test]
	public void S42PersistsSquareIntoLaterTremolo()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerTremoloWaveformPatternEffect(2)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new TremoloPatternEffect(0x53)),
			context,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetTremoloCommand(
					5,
					3,
					TrackerWaveform.Square)));
	}

	[Test]
	public void S33AndS43SelectRandom()
	{
		SequencingContext context = new();

		DataPatternDefinition select = Pattern(1);
		PatternCell cell = select.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerVibratoWaveformPatternEffect(3));
		cell.Effects.Add(new TrackerTremoloWaveformPatternEffect(3));

		PatternNoteProcessor.GenerateNotes(
			select,
			context,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0).VibratoWaveform,
			Is.EqualTo(TrackerWaveform.Random));
		Assert.That(
			context.GetPhysicalChannelState(0).TremoloWaveform,
			Is.EqualTo(TrackerWaveform.Random));
	}

	[Test]
	public void WaveformValuesAboveThreeAreIgnoredLikeImpulseTracker()
	{
		SequencingContext context = new();

		DataPatternDefinition initial = Pattern(1);
		PatternCell first = initial.Grid.GetOrCreateCell(0, 0);
		first.Effects.Add(new TrackerVibratoWaveformPatternEffect(1));
		first.Effects.Add(new TrackerTremoloWaveformPatternEffect(2));
		PatternNoteProcessor.GenerateNotes(
			initial,
			context,
			new NoteScheduleBuilder(),
			out _);

		DataPatternDefinition ignored = Pattern(1);
		PatternCell second = ignored.Grid.GetOrCreateCell(0, 0);
		second.Effects.Add(new TrackerVibratoWaveformPatternEffect(0x0F));
		second.Effects.Add(new TrackerTremoloWaveformPatternEffect(0x0E));
		PatternNoteProcessor.GenerateNotes(
			ignored,
			context,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0).VibratoWaveform,
			Is.EqualTo(TrackerWaveform.RampDown));
		Assert.That(
			context.GetPhysicalChannelState(0).TremoloWaveform,
			Is.EqualTo(TrackerWaveform.Square));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedWaveformSelection()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVibratoWaveformPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new VibratoPatternEffect(0x53));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.Sine)));
	}

	[Test]
	public void FlattenedChildDoesNotInheritParentVibratoWaveform()
	{
		SequencingContext parent =
			new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerVibratoWaveformPatternEffect(2),
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new VibratoPatternEffect(0x53)),
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.Sine)));
	}

	[Test]
	public void MixdownChildHasIndependentWaveformSelection()
	{
		SequencingContext parent = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerVibratoWaveformPatternEffect(1)),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext mixdown = parent.MixdownChild();

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(new VibratoPatternEffect(0x53)),
			mixdown,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.Sine)));
	}

	[Test]
	public void WaveformNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerVibratoWaveformPatternEffect(16));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerTremoloWaveformPatternEffect(16));
	}

	private static DataPatternDefinition Pattern(int rows)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = 1,
		};

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
