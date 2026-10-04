using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PatternDelayNoteDelayTests
{
	[Test]
	public void SD3NextToSE2StartsOnEveryDelayedRowRepetition()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(3));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteEvent[] starts = output.Freeze()
			.Where(e =>
				e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(360)));
		Assert.That(starts.Length, Is.EqualTo(3));
		Assert.That(
			starts.Select(e => e.Offset.TimeOffset),
			Is.EqualTo(new[]
			{
				TimeSpan.FromMilliseconds(60),
				TimeSpan.FromMilliseconds(180),
				TimeSpan.FromMilliseconds(300),
			}));
	}

	[Test]
	public void DelayedAtomicNoteSetupRepeatsWithEachRowRepetition()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		cell.Effects.Add(new SampleOffsetPatternEffect(0x01));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(2));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(2));

		foreach (NoteEvent noteEvent in schedule)
		{
			Assert.That(noteEvent.Commands.Count, Is.EqualTo(3));
			Assert.That(
				noteEvent.Commands[0],
				Is.TypeOf<StartNoteCommand>());
			Assert.That(
				noteEvent.Commands[1],
				Is.EqualTo(new SetNoteVolumeCommand(0.5)));
			Assert.That(
				noteEvent.Commands[2],
				Is.EqualTo(new SetSourceFrameOffsetCommand(0x0100)));
		}

		Assert.That(
			schedule[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(40)));
		Assert.That(
			schedule[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(160)));
	}

	[Test]
	public void S6xExtendsEachSpanBeforeRepeatedSDxTrigger()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(3));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(8));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteEvent[] starts = output.Freeze()
			.Where(e =>
				e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(360)));
		Assert.That(starts.Length, Is.EqualTo(2));
		Assert.That(
			starts[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(160)));
		Assert.That(
			starts[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(340)));
	}

	[Test]
	public void SExDoesNotMakeOutOfRangeSDxBecomeReachable()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(6));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(3));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(
			duration,
			Is.EqualTo(TimeSpan.FromMilliseconds(480)));
		Assert.That(output.Freeze().Count, Is.EqualTo(0));
	}

	[Test]
	public void OrdinaryNoteNextToSE2StillStartsOnlyOnce()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent[] starts = output.Freeze()
			.Where(e =>
				e.Commands.Any(c => c is StartNoteCommand))
			.ToArray();

		Assert.That(starts.Length, Is.EqualTo(1));
		Assert.That(
			starts[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void SlideNextToSDxReinitializesAtDelayedPointOnEachRepeat()
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote((ObjectId)10U);
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x01));
		cell.Effects.Add(new TrackerNoteDelayPatternEffect(3));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		NoteEvent[] slideStarts = schedule
			.Where(e =>
				e.Commands.Any(
					c => c is SetNoteVolumeSlideCommand))
			.ToArray();

		Assert.That(slideStarts.Length, Is.EqualTo(2));
		Assert.That(
			slideStarts[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(
			slideStarts[1].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(180)));

		Assert.That(
			schedule.Any(e =>
				e.Offset.TimeOffset
					== TimeSpan.FromMilliseconds(120)
				&& e.Commands.Any(
					c => c is SetNoteVolumeSlideCommand)),
			Is.False);
	}

	private static DataPatternDefinition Pattern()
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
}
