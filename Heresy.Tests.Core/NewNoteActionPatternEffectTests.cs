using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class NewNoteActionPatternEffectTests
{
	[TestCase(NoteDisplacementAction.Cut)]
	[TestCase(NoteDisplacementAction.Continue)]
	[TestCase(NoteDisplacementAction.Off)]
	[TestCase(NoteDisplacementAction.Fade)]
	public void DataGridTranslatesS73ThroughS76ToRawCommand(
		NoteDisplacementAction action)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerNewNoteActionPatternEffect(action));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyTrackerNewNoteActionCommand(action)));
	}

	[TestCase(NoteDisplacementAction.Cut)]
	[TestCase(NoteDisplacementAction.Continue)]
	[TestCase(NoteDisplacementAction.Off)]
	[TestCase(NoteDisplacementAction.Fade)]
	public void ProcessorResolvesNewNoteActionOverride(
		NoteDisplacementAction action)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerNewNoteActionPatternEffect(action));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetCurrentVoiceDisplacementActionCommand(action)));
	}

	[Test]
	public void SameCellNewNotePrecedesNewNoteActionOverride()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerNewNoteActionPatternEffect(
				NoteDisplacementAction.Continue));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteEvent noteEvent = output.Freeze()[0];

		Assert.That(
			noteEvent.Commands[0],
			Is.EqualTo(new StartNoteCommand(sourceId)));
		Assert.That(
			noteEvent.Commands[1],
			Is.EqualTo(
				new SetCurrentVoiceDisplacementActionCommand(
					NoteDisplacementAction.Continue)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedNewNoteActionOverride()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerNewNoteActionPatternEffect(
				NoteDisplacementAction.Continue));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);

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
				.OfType<SetCurrentVoiceDisplacementActionCommand>(),
			Is.Empty);
	}

	[Test]
	public void PatternLoopExecutesNewNoteActionOverrideOnEveryVisit()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		row0.Effects.Add(
			new TrackerNewNoteActionPatternEffect(
				NoteDisplacementAction.Continue));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetCurrentVoiceDisplacementActionCommand[] actions =
			output.Freeze()
				.SelectMany(e => e.Commands)
				.OfType<SetCurrentVoiceDisplacementActionCommand>()
				.ToArray();

		Assert.That(actions.Length, Is.EqualTo(2));
	}

	[Test]
	public void NewNoteActionPatternEffectRejectsUndefinedEnumValue()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerNewNoteActionPatternEffect(
				(NoteDisplacementAction)4));
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
