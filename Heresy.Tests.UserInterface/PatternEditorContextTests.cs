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
}
