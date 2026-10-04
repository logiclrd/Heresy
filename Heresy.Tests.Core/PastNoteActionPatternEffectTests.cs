using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PastNoteActionPatternEffectTests
{
	[TestCase(TrackerPastNoteAction.Cut)]
	[TestCase(TrackerPastNoteAction.Off)]
	[TestCase(TrackerPastNoteAction.Fade)]
	public void DataGridTranslatesS70ThroughS72ToRawCommand(
		TrackerPastNoteAction action)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(action));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new ApplyTrackerPastNoteActionCommand(action)));
	}

	[TestCase(TrackerPastNoteAction.Cut)]
	[TestCase(TrackerPastNoteAction.Off)]
	[TestCase(TrackerPastNoteAction.Fade)]
	public void ProcessorResolvesPastNoteAction(
		TrackerPastNoteAction action)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(action));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyPastNoteActionCommand(action)));
	}

	[Test]
	public void SameCellNewNotePrecedesPastNoteAction()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerPastNoteActionPatternEffect(
				TrackerPastNoteAction.Cut));

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
				new ApplyPastNoteActionCommand(
					TrackerPastNoteAction.Cut)));
	}

	[Test]
	public void FlattenedChildMapsPastNoteActionToParentPhysicalChannel()
	{
		SequencingContext parent =
			new(physicalChannelBase: 3);
		SequencingContext child =
			parent.FlattenedChild(
				physicalChannelOffset: 4);

		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(
				TrackerPastNoteAction.Off));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Target,
			Is.EqualTo(ChannelTarget.Physical(7)));
	}

	[Test]
	public void MixdownChildUsesIndependentLocalPhysicalChannel()
	{
		SequencingContext parent =
			new(physicalChannelBase: 5);
		SequencingContext child =
			parent.MixdownChild();

		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(
				TrackerPastNoteAction.Fade));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			child,
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Target,
			Is.EqualTo(ChannelTarget.Physical(0)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedPastNoteAction()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(
				TrackerPastNoteAction.Cut));
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
				.OfType<ApplyPastNoteActionCommand>(),
			Is.Empty);
	}

	[Test]
	public void PatternLoopExecutesPastNoteActionOnEveryVisit()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		PatternCell row0 = pattern.Grid.GetOrCreateCell(0, 0);
		row0.Effects.Add(
			new TrackerPatternLoopPatternEffect(0));
		row0.Effects.Add(
			new TrackerPastNoteActionPatternEffect(
				TrackerPastNoteAction.Cut));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerPatternLoopPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		ApplyPastNoteActionCommand[] actions = output.Freeze()
			.SelectMany(e => e.Commands)
			.OfType<ApplyPastNoteActionCommand>()
			.ToArray();

		Assert.That(actions.Length, Is.EqualTo(2));
	}

	[Test]
	public void PastNoteActionRejectsUndefinedEnumValue()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerPastNoteActionPatternEffect(
				(TrackerPastNoteAction)3));
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
