using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PanbrelloWaveformPatternEffectTests
{
	[Test]
	public void DataGridTranslatesS5xToRawCommand()
	{
		DataPatternDefinition pattern = Pattern(1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanbrelloWaveformPatternEffect(2));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyTrackerPanbrelloWaveformCommand(2)));
	}

	[TestCase((byte)0, TrackerWaveform.Sine)]
	[TestCase((byte)1, TrackerWaveform.RampDown)]
	[TestCase((byte)2, TrackerWaveform.Square)]
	[TestCase((byte)3, TrackerWaveform.Random)]
	public void S50ThroughS53SelectWaveformAndEmitPhaseReset(
		byte value,
		TrackerWaveform expected)
	{
		SequencingContext context = new();
		NoteSchedule schedule = Generate(
			new TrackerPanbrelloWaveformPatternEffect(value),
			context);

		Assert.That(
			context.GetPhysicalChannelState(0).PanbrelloWaveform,
			Is.EqualTo(expected));
		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetPanbrelloWaveformCommand(expected),
				}));
	}

	[Test]
	public void S54ThroughS5FAreIgnoredLikeImpulseTracker()
	{
		SequencingContext context = new();

		Generate(
			new TrackerPanbrelloWaveformPatternEffect(2),
			context);

		NoteSchedule schedule = Generate(
			new TrackerPanbrelloWaveformPatternEffect(0x0F),
			context);

		Assert.That(
			context.GetPhysicalChannelState(0).PanbrelloWaveform,
			Is.EqualTo(TrackerWaveform.Sine));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloWaveformCommand>(),
			Is.Empty);
	}

	[Test]
	public void LaterYxxUsesPersistentS5xWaveform()
	{
		SequencingContext context = new();

		Generate(
			new TrackerPanbrelloWaveformPatternEffect(1),
			context);

		NoteSchedule schedule = Generate(
			new PanbrelloPatternEffect(0x53),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single(),
			Is.EqualTo(
				new SetPanbrelloCommand(
					5,
					3,
					TrackerWaveform.RampDown)));
	}

	[Test]
	public void SameRowS5xBeforeYxxResetsPhaseBeforeStartingSelectedWaveform()
	{
		DataPatternDefinition pattern = Pattern(1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(
			new TrackerPanbrelloWaveformPatternEffect(2));
		cell.Effects.Add(
			new PanbrelloPatternEffect(0x53));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetPanbrelloWaveformCommand(
						TrackerWaveform.Square),
					new SetPanbrelloCommand(
						5,
						3,
						TrackerWaveform.Square),
				}));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedS5x()
	{
		DataPatternDefinition pattern = Pattern(2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPanbrelloWaveformPatternEffect(2));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new PanbrelloPatternEffect(0x53));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloWaveformCommand>(),
			Is.Empty);
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single()
				.Waveform,
			Is.EqualTo(TrackerWaveform.Sine));
	}

	[Test]
	public void FlattenedChildDoesNotInheritParentPanbrelloWaveform()
	{
		SequencingContext parent =
			new(physicalChannelBase: 3);

		Generate(
			new TrackerPanbrelloWaveformPatternEffect(2),
			parent,
			channel: 4);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteSchedule schedule = Generate(
			new PanbrelloPatternEffect(0x53),
			child);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single()
				.Waveform,
			Is.EqualTo(TrackerWaveform.Square));
	}

	[Test]
	public void MixdownChildHasIndependentPanbrelloWaveformSelection()
	{
		SequencingContext parent = new();

		Generate(
			new TrackerPanbrelloWaveformPatternEffect(1),
			parent);

		SequencingContext mixdown = parent.MixdownChild();

		NoteSchedule schedule = Generate(
			new PanbrelloPatternEffect(0x53),
			mixdown);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetPanbrelloCommand>()
				.Single()
				.Waveform,
			Is.EqualTo(TrackerWaveform.Sine));
	}

	[Test]
	public void S5xNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerPanbrelloWaveformPatternEffect(16));
	}

	private static NoteSchedule Generate(
		PatternEffect effect,
		SequencingContext context,
		int channel = 0)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(effect);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
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
