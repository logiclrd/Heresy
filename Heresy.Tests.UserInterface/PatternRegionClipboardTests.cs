using System;
using System.Linq;

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
public sealed class PatternRegionClipboardTests
{
	[Test]
	public void ClipboardCodecRoundTripsFullAndEmptyAvailableCells()
	{
		PatternRegionClipboardData data =
			new(
				RowCount: 2,
				ChannelCount: 2,
				Cells:
				[
					new PatternRegionClipboardCell(
						0,
						0,
						new StartPatternNote(
							(ObjectId)17U,
							1.25,
							0.75,
							true),
						(ObjectId)23U,
						0.5,
						[
							new VibratoPatternEffect(0x12),
							new EmptyTrackerPatternEffect(0x34),
						]),
					new PatternRegionClipboardCell(
						0,
						1,
						null,
						ObjectId.None,
						null,
						[]),
					new PatternRegionClipboardCell(
						1,
						0,
						new PatternNoteOff(),
						ObjectId.None,
						null,
						[new SetSpeedPatternEffect(6)]),
				]);

		string text =
			PatternRegionClipboardCodec.Serialize(data);
		PatternRegionClipboardData decoded =
			PatternRegionClipboardCodec.Deserialize(text);

		decoded.RowCount.Should().Be(2);
		decoded.ChannelCount.Should().Be(2);
		decoded.Cells.Should().HaveCount(3);
		decoded.Cells[0].Should().BeEquivalentTo(data.Cells[0]);
		decoded.Cells[1].Should().BeEquivalentTo(data.Cells[1]);
		decoded.Cells[2].Should().BeEquivalentTo(data.Cells[2]);
	}

	[Test]
	public void CapturePreservesEmptyCellsButSkipsUnavailableSequenceCells()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Wide",
				rowCount: 1,
				channelCount: 3);
		wide.Grid.GetOrCreateCell(0, 2).Note =
			new PatternNoteOff();
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Narrow",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		PatternRegionClipboardData data =
			PatternRegionClipboardEditor.Capture(
				context,
				new PatternSelectionRegion(0, 0, 1, 2));

		data.RowCount.Should().Be(2);
		data.ChannelCount.Should().Be(3);
		data.Cells.Select(cell =>
				(cell.RowOffset, cell.ChannelOffset))
			.Should().Equal(
				(0, 0),
				(0, 1),
				(0, 2),
				(1, 0));
		data.Cells.Single(cell =>
				cell.RowOffset == 0
					&& cell.ChannelOffset == 2)
			.Note.Should().Be(new PatternNoteOff());
		data.Cells.Single(cell =>
				cell.RowOffset == 0
					&& cell.ChannelOffset == 0)
			.IsEmpty.Should().BeTrue();
	}

	[Test]
	public void MergePasteCopiesOnlyPopulatedFields()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 3,
				channelCount: 2);
		PatternCell destination =
			pattern.Grid.GetOrCreateCell(1, 0);
		destination.Note = new PatternNoteCut();
		destination.SourceId = (ObjectId)41U;
		destination.Volume = 0.25;
		destination.Effects.Add(
			new TonePortamentoPatternEffect(0x22));
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(1, 0, PatternCellField.Note);
		PatternRegionClipboardData data =
			new(
				1,
				1,
				[
					new PatternRegionClipboardCell(
						0,
						0,
						new StartPatternNote(1.5),
						ObjectId.None,
						null,
						[new VibratoPatternEffect(0x12)]),
				]);

		bool changed =
			PatternRegionClipboardEditor.Paste(
				workspace,
				context,
				cursor,
				data,
				PatternRegionPasteMode.Merge);

		changed.Should().BeTrue();
		destination.Note.Should().Be(new StartPatternNote(1.5));
		destination.SourceId.Should().Be((ObjectId)41U);
		destination.Volume.Should().Be(0.25);
		destination.Effects.Should().Equal(
			new VibratoPatternEffect(0x12));
	}

	[Test]
	public void MergePasteOfEmptyAvailableCellDoesNothing()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 1);
		PatternCell destination =
			pattern.Grid.GetOrCreateCell(0, 0);
		destination.Note = new PatternNoteCut();
		destination.Volume = 0.5;
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);
		PatternRegionClipboardData data =
			new(
				1,
				1,
				[
					new PatternRegionClipboardCell(
						0,
						0,
						null,
						ObjectId.None,
						null,
						[]),
				]);
		uint revision = workspace.Document.DocumentRevision;

		PatternRegionClipboardEditor.Paste(
				workspace,
				context,
				cursor,
				data,
				PatternRegionPasteMode.Merge)
			.Should().BeFalse();

		destination.Note.Should().Be(new PatternNoteCut());
		destination.Volume.Should().Be(0.5);
		workspace.Document.DocumentRevision.Should().Be(revision);
	}

	[Test]
	public void OverwritePasteReproducesEmptyFieldsAndClearsCell()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 1);
		PatternCell destination =
			pattern.Grid.GetOrCreateCell(0, 0);
		destination.Note = new PatternNoteCut();
		destination.SourceId = (ObjectId)99U;
		destination.Volume = 0.5;
		destination.Effects.Add(new SetSpeedPatternEffect(6));
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);
		PatternRegionClipboardData data =
			new(
				1,
				1,
				[
					new PatternRegionClipboardCell(
						0,
						0,
						null,
						ObjectId.None,
						null,
						[]),
				]);

		PatternRegionClipboardEditor.Paste(
				workspace,
				context,
				cursor,
				data,
				PatternRegionPasteMode.Overwrite)
			.Should().BeTrue();

		pattern.Grid[0, 0].Should().BeNull();
	}

	[Test]
	public void PasteClipsAtContextAndPerRowChannelEdges()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Wide",
				rowCount: 1,
				channelCount: 3);
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Narrow",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);
		PatternEffectCursor cursor =
			new(0, 2, PatternCellField.Note);
		PatternRegionClipboardData data =
			new(
				2,
				2,
				[
					Cell(0, 0, new PatternNoteCut()),
					Cell(0, 1, new PatternNoteOff()),
					Cell(1, 0, new StartPatternNote()),
					Cell(1, 1, new StartPatternNote(2.0)),
				]);

		PatternRegionClipboardEditor.Paste(
			workspace,
			context,
			cursor,
			data,
			PatternRegionPasteMode.Overwrite);

		wide.Grid[0, 2]!.Note.Should().Be(new PatternNoteCut());
		narrow.Grid[0, 0].Should().BeNull();
	}

	[Test]
	public void ClearRegionRemovesEveryAvailableCellAndMarksOneDocumentChange()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 4,
				channelCount: 3);
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(1, 1).Volume = 0.5;
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new EmptyTrackerPatternEffect(0x12));
		pattern.Grid.GetOrCreateCell(2, 1).Effects.Add(
			new SetSpeedPatternEffect(6));
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				workspace.Document,
				pattern);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		PatternRegionClipboardEditor.Clear(
				workspace,
				context,
				new PatternSelectionRegion(1, 0, 2, 1))
			.Should().BeTrue();

		pattern.Grid[1, 0].Should().BeNull();
		pattern.Grid[1, 1].Should().BeNull();
		pattern.Grid[2, 0].Should().BeNull();
		pattern.Grid[2, 1].Should().BeNull();
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[TestCase(Key.C, KeyModifiers.Control, PatternRegionClipboardCommand.Copy)]
	[TestCase(Key.X, KeyModifiers.Control, PatternRegionClipboardCommand.Cut)]
	[TestCase(Key.V, KeyModifiers.Control, PatternRegionClipboardCommand.PasteMerge)]
	[TestCase(Key.V, KeyModifiers.Control | KeyModifiers.Shift, PatternRegionClipboardCommand.PasteOverwrite)]
	[TestCase(Key.Delete, KeyModifiers.Control, PatternRegionClipboardCommand.Clear)]
	public void KeyboardMapsRegionClipboardCommands(
		Key key,
		KeyModifiers modifiers,
		PatternRegionClipboardCommand expected)
	{
		PatternRegionClipboardKeyboard.TryGetCommand(
			key,
			modifiers,
			out PatternRegionClipboardCommand actual).Should().BeTrue();

		actual.Should().Be(expected);
	}

	[Test]
	public void KeyboardLeavesAltAndUnassignedShiftVariantsAlone()
	{
		PatternRegionClipboardKeyboard.TryGetCommand(
			Key.C,
			KeyModifiers.Control | KeyModifiers.Alt,
			out _).Should().BeFalse();
		PatternRegionClipboardKeyboard.TryGetCommand(
			Key.X,
			KeyModifiers.Control | KeyModifiers.Shift,
			out _).Should().BeFalse();
	}

	private static PatternRegionClipboardCell Cell(
		int row,
		int channel,
		PatternNoteEntry note)
		=> new(
			row,
			channel,
			note,
			ObjectId.None,
			null,
			[]);
}
