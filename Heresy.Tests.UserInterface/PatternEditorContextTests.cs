using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEditorContextTests
{
	[Test]
	public void SinglePatternContextMapsRowsDirectlyToTheLivePattern()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 4,
				channelCount: 3);

		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);

		context.IsSequence.Should().BeFalse();
		context.Rows.Should().HaveCount(4);
		context.InitialDisplayRow.Should().Be(0);
		context.MaxChannelCount.Should().Be(3);
		context.Rows.Select(row => row.Pattern)
			.Should().OnlyContain(item => ReferenceEquals(item, pattern));
		context.Rows.Select(row => row.PatternRow)
			.Should().Equal(0, 1, 2, 3);
		context.Rows.Should().OnlyContain(row => row.SequenceEntryIndex == null);
	}

	[Test]
	public void SequenceContextFlattensAdjacentDataPatternsAndRespectsStartRows()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 4,
				channelCount: 2);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 5,
				channelCount: 4);
		DataPatternDefinition third =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Third",
				rowCount: 3,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id, 1));
		sequence.Entries.Add(new SequenceEntry(second.Id, 2));
		sequence.Entries.Add(new SequenceEntry(third.Id, 0));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);

		context.IsSequence.Should().BeTrue();
		context.Rows.Select(row =>
				(row.Pattern.Name, row.PatternRow, row.SequenceEntryIndex))
			.Should().Equal(
				("First", 1, (int?)0),
				("First", 2, (int?)0),
				("First", 3, (int?)0),
				("Second", 2, (int?)1),
				("Second", 3, (int?)1),
				("Second", 4, (int?)1),
				("Third", 0, (int?)2),
				("Third", 1, (int?)2),
				("Third", 2, (int?)2));
		context.InitialDisplayRow.Should().Be(3);
		context.MaxChannelCount.Should().Be(4);
	}

	[Test]
	public void PlaybackCursorPreservesLocalRowAndSequenceOccurrence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 3,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 5,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id, startRow: 2));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);

		context.GetPlaybackCursor(4)
			.Should().Be(
				new PatternEditorPlaybackCursor(
					second.Id,
					3,
					sequence.Id,
					1));
	}

	[Test]
	public void RepeatedPatternOccurrencesResolveToSameSharedPatternObject()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Shared",
				rowCount: 3,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 0));
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 1));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.Rows.Should().HaveCount(5);
		context.Rows.Should().OnlyContain(
			row => ReferenceEquals(row.Pattern, pattern));
		context.FindDisplayRows(pattern, patternRow: 1)
			.Should().Equal(1, 3);
	}

	[Test]
	public void MissingAndScriptEntriesRemainSegmentsButContributeNoEditableRows()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition data =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Data",
				rowCount: 2,
				channelCount: 2);
		ObjectId scriptId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(scriptId, "Script"));
		DataPatternDefinition deleted =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Deleted",
				rowCount: 2,
				channelCount: 2);
		ObjectId deletedId = deleted.Id;
		workspace.Document.Remove(deletedId);

		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(data.Id));
		sequence.Entries.Add(new SequenceEntry(scriptId));
		sequence.Entries.Add(new SequenceEntry(deletedId));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.Rows.Should().HaveCount(2);
		context.Segments.Should().HaveCount(3);
		context.Segments[0].Pattern.Should().BeSameAs(data);
		context.Segments[1].Pattern.Should().BeNull();
		context.Segments[1].Status.Should().Contain("Script");
		context.Segments[2].Pattern.Should().BeNull();
		context.Segments[2].Status.Should().Contain("missing");
	}

	[Test]
	public void RefreshCanPreserveSequenceOccurrenceAndLocalPatternRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 3,
				channelCount: 2);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 4,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEditorRow preferred = context.Rows[4]; // Second row 1.

		second.RowCount = 6;
		int displayRow = context.Refresh(preferred);

		context.Rows.Should().HaveCount(9);
		context.Rows[displayRow].SequenceEntryIndex.Should().Be(1);
		context.Rows[displayRow].Pattern.Should().BeSameAs(second);
		context.Rows[displayRow].PatternRow.Should().Be(1);
	}

	[Test]
	public void SequenceInitialFocusFallsForwardWhenSelectedEntryHasNoRows()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition empty =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Empty",
				rowCount: 2,
				channelCount: 2);
		DataPatternDefinition next =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Next",
				rowCount: 2,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(empty.Id, startRow: 5));
		sequence.Entries.Add(new SequenceEntry(next.Id));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.InitialDisplayRow.Should().Be(0);
		context.Rows[0].SequenceEntryIndex.Should().Be(1);
	}
	[Test]
	public void MappedNoteEntryAdvancesAcrossPatternBoundaryAndEditsRealObjects()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 1,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);
		PatternNoteInputState noteState =
			new(ObjectId.None, baseOctave: 4);

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternNoteKeyboardEditor.Type(
				workspace,
				row.Pattern,
				cursor,
				noteState,
				'Z'));

		cursor.Row.Should().Be(1);
		first.Grid[0, 0]!.Note.Should().Be(new StartPatternNote());

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternNoteKeyboardEditor.Type(
				workspace,
				row.Pattern,
				cursor,
				noteState,
				'X'));

		((StartPatternNote)second.Grid[0, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(
				System.Math.Pow(2.0, 2.0 / 12.0),
				1e-12);
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void MappedEffectStackMutationTargetsUnderlyingPatternRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 2,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 2,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id, startRow: 1));
		sequence.Entries.Add(new SequenceEntry(second.Id, startRow: 1));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEffectCursor cursor =
			new(1, 0, PatternCellField.EffectCommand);

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternEffectStackEditor.InsertBefore(
				workspace,
				row.Pattern,
				cursor));

		first.Grid[1, 0].Should().BeNull();
		second.Grid[1, 0]!.Effects.Should().ContainSingle()
			.Which.Should().Be(new EmptyTrackerPatternEffect());
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void VerticalNavigationAcrossBoundaryClampsToDestinationChannelCount()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Wide",
				rowCount: 1,
				channelCount: 4);
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Narrow",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);
		PatternEffectCursor cursor =
			new(0, 3, PatternCellField.Note);

		PatternEditorContextCursor.MoveDown(context, cursor);

		cursor.Row.Should().Be(1);
		cursor.Channel.Should().Be(0);
	}

	[Test]
	public void GlobalNavigationMovesToStandalonePatternCorners()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 5,
				channelCount: 4);
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(2, 2, PatternCellField.Volume);

		PatternEditorContextCursor.MoveToTopLeft(
			context,
			cursor);

		cursor.Row.Should().Be(0);
		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Note);

		PatternEditorContextCursor.MoveToBottomRight(
			context,
			cursor);

		cursor.Row.Should().Be(4);
		cursor.Channel.Should().Be(3);
		cursor.Field.Should().Be(PatternCellField.EffectParameter);
	}

	[Test]
	public void GlobalNavigationSpansEntireSequenceEditorContext()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 2,
				channelCount: 4);
		DataPatternDefinition last =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Last",
				rowCount: 3,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id, startRow: 1));
		sequence.Entries.Add(new SequenceEntry(last.Id, startRow: 1));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEffectCursor cursor =
			new(1, 1, PatternCellField.Source);

		PatternEditorContextCursor.MoveToTopLeft(
			context,
			cursor);

		cursor.Row.Should().Be(0);
		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Note);

		PatternEditorContextCursor.MoveToBottomRight(
			context,
			cursor);

		cursor.Row.Should().Be(2);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.EffectParameter);
	}

	[Test]
	public void GlobalBottomRightUsesNativeEffectAsOneWholeFinalField()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 2);
		pattern.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new SetPlaybackFrequencyPatternEffect(440));
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);

		PatternEditorContextCursor.MoveToBottomRight(
			context,
			cursor);

		cursor.Row.Should().Be(1);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);
	}

	[Test]
	public void HomeEndNavigationUsesCurrentSequencePatternCellAndWidth()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 1,
				channelCount: 4);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 1,
				channelCount: 2);
		second.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetPlaybackFrequencyPatternEffect(440));
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEffectCursor cursor =
			new(1, 0, PatternCellField.Volume);

		PatternEditorContextCursor.MoveEnd(
			context,
			cursor);

		cursor.Row.Should().Be(1);
		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);

		PatternEditorContextCursor.MoveEnd(
			context,
			cursor);

		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);

		PatternEditorContextCursor.MoveHome(
			context,
			cursor);

		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Note);

		PatternEditorContextCursor.MoveHome(
			context,
			cursor);

		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Note);
	}

	[Test]
	public void AdjacentChannelNavigationUsesCurrentSequencePatternWidth()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Wide",
				rowCount: 1,
				channelCount: 4);
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Narrow",
				rowCount: 1,
				channelCount: 2);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);
		PatternEffectCursor cursor =
			new(1, 1, PatternCellField.Source);

		PatternEditorContextCursor.MoveChannel(
			context,
			cursor,
			delta: 1);

		cursor.Row.Should().Be(1);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Source);

		PatternEditorContextCursor.MoveChannel(
			context,
			cursor,
			delta: -1);

		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Source);
	}

	[Test]
	public void SequencePatternNavigationMovesBetweenEditableOccurrences()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 2,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 3,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));
		sequence.Entries.Add(new SequenceEntry(first.Id, startRow: 1));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 1,
				delta: 1)
			.Should().Be(2);
		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 3,
				delta: -1)
			.Should().Be(0);
		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 3,
				delta: 1)
			.Should().Be(5);
	}

	[Test]
	public void SequencePatternNavigationSkipsNonEditableSegments()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 1,
				channelCount: 1);
		ObjectId scriptId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(scriptId, "Script"));
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(scriptId));
		sequence.Entries.Add(new SequenceEntry(second.Id));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 0,
				delta: 1)
			.Should().Be(1);
		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 1,
				delta: -1)
			.Should().Be(0);
	}

	[Test]
	public void SequencePatternNavigationStopsAtSequenceEdges()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 1,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 0,
				delta: -1)
			.Should().BeNull();
		context.FindAdjacentPatternDisplayRow(
				currentDisplayRow: 1,
				delta: 1)
			.Should().BeNull();
	}

}
