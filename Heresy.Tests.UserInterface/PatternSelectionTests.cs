using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSelectionTests
{
	[Test]
	public void AltBAndAltESetOppositeSelectionCorners()
	{
		PatternSelectionState selection = new();

		selection.SetStart(row: 5, channel: 3);
		selection.SetEnd(row: 2, channel: 1);

		selection.Region.Should().Be(
			new PatternSelectionRegion(
				TopRow: 2,
				LeftChannel: 1,
				BottomRow: 5,
				RightChannel: 3));
	}

	[Test]
	public void ShiftArrowStartsAtOriginalCellAndExtendsToMovedCell()
	{
		PatternSelectionState selection = new();

		selection.Extend(
			originalRow: 4,
			originalChannel: 2,
			newRow: 5,
			newChannel: 2);
		selection.Extend(
			originalRow: 5,
			originalChannel: 2,
			newRow: 5,
			newChannel: 3);

		selection.Region.Should().Be(
			new PatternSelectionRegion(4, 2, 5, 3));
	}

	[Test]
	public void AltDSelectsMajorHighlightRowsAndRepeatedUseDoublesHeight()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 32,
				channelCount: 4);
		pattern.MajorHighlightRows = 4;
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(3, 2, PatternCellField.Source);
		PatternSelectionState selection = new();

		PatternSelectionEditor.SelectMajorBlock(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(3, 2, 6, 2));

		PatternSelectionEditor.SelectMajorBlock(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(3, 2, 10, 2));

		PatternSelectionEditor.SelectMajorBlock(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(3, 2, 18, 2));
	}

	[Test]
	public void AltDResetsToCursorWhenCursorIsOutsideExistingBlock()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 32,
				channelCount: 4);
		pattern.MajorHighlightRows = 4;
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(10, 1, PatternCellField.Note);
		PatternSelectionState selection = new();
		selection.SetStart(0, 0);
		selection.SetEnd(3, 0);

		PatternSelectionEditor.SelectMajorBlock(
			context,
			cursor,
			selection);

		selection.Region.Should().Be(
			new PatternSelectionRegion(10, 1, 13, 1));
	}

	[Test]
	public void AltDClampsToCurrentSequencePatternOccurrence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 8,
				channelCount: 2);
		first.MajorHighlightRows = 4;
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 8,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(new SequenceEntry(first.Id, startRow: 6));
		sequence.Entries.Add(new SequenceEntry(second.Id, startRow: 3));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);
		PatternEffectCursor cursor =
			new(0, 1, PatternCellField.Note);
		PatternSelectionState selection = new();

		PatternSelectionEditor.SelectMajorBlock(
			context,
			cursor,
			selection);

		selection.Region.Should().Be(
			new PatternSelectionRegion(0, 1, 1, 1));
		context.GetRow(1).Pattern.Should().BeSameAs(first);
		context.GetRow(2).Pattern.Should().BeSameAs(second);
	}

	[Test]
	public void AltLSelectsCurrentColumnThenWholePatternOccurrence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 6,
				channelCount: 4);
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(2, 1, PatternCellField.Volume);
		PatternSelectionState selection = new();

		PatternSelectionEditor.SelectColumnOrPattern(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(0, 1, 5, 1));

		PatternSelectionEditor.SelectColumnOrPattern(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(0, 0, 5, 3));
	}

	[Test]
	public void AltLUsesOnlyCurrentSequenceOccurrenceRowsAndWidth()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Wide",
				rowCount: 3,
				channelCount: 4);
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Narrow",
				rowCount: 4,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id, startRow: 2));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEffectCursor cursor =
			new(3, 1, PatternCellField.Note);
		PatternSelectionState selection = new();

		PatternSelectionEditor.SelectColumnOrPattern(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(3, 1, 4, 1));

		PatternSelectionEditor.SelectColumnOrPattern(
			context,
			cursor,
			selection);
		selection.Region.Should().Be(
			new PatternSelectionRegion(3, 0, 4, 1));
	}

	[Test]
	public void AltUClearsSelection()
	{
		PatternSelectionState selection = new();
		selection.SetStart(1, 2);
		selection.SetEnd(3, 4);

		selection.Clear();

		selection.Region.Should().BeNull();
	}

	[TestCase(Key.B, PatternSelectionCommand.SetStart)]
	[TestCase(Key.E, PatternSelectionCommand.SetEnd)]
	[TestCase(Key.D, PatternSelectionCommand.SelectMajorBlock)]
	[TestCase(Key.L, PatternSelectionCommand.SelectColumnOrPattern)]
	[TestCase(Key.U, PatternSelectionCommand.Clear)]
	public void AltLetterKeysMapToSelectionCommands(
		Key key,
		PatternSelectionCommand expected)
	{
		PatternSelectionKeyboard.TryGetCommand(
			key,
			KeyModifiers.Alt,
			out PatternSelectionCommand actual).Should().BeTrue();
		actual.Should().Be(expected);
	}

	[TestCase(Key.Left, PatternSelectionCommand.ExtendLeft)]
	[TestCase(Key.Right, PatternSelectionCommand.ExtendRight)]
	[TestCase(Key.Up, PatternSelectionCommand.ExtendUp)]
	[TestCase(Key.Down, PatternSelectionCommand.ExtendDown)]
	public void ShiftArrowsMapToSelectionExtension(
		Key key,
		PatternSelectionCommand expected)
	{
		PatternSelectionKeyboard.TryGetCommand(
			key,
			KeyModifiers.Shift,
			out PatternSelectionCommand actual).Should().BeTrue();
		actual.Should().Be(expected);
	}

	[Test]
	public void ExtraModifiersDoNotStealSelectionShortcuts()
	{
		PatternSelectionKeyboard.TryGetCommand(
			Key.B,
			KeyModifiers.Alt | KeyModifiers.Control,
			out _).Should().BeFalse();
		PatternSelectionKeyboard.TryGetCommand(
			Key.Down,
			KeyModifiers.Shift | KeyModifiers.Alt,
			out _).Should().BeFalse();
	}
}
