using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneGridBatchEditorTests
{
	[AvaloniaTest]
	public void SourceSelectionAcrossRectanglePublishesMultipleDraftsInOneRevision()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "UI source");
		ObjectId sourceId = AddSource(workspace);
		InstrumentEditorControl editor = new(workspace, instrument,
			() => { }, _ => { });
		InstrumentToneGridModel model = Field<InstrumentToneGridModel>(editor, "_model");
		model.SetRow(1, new(PitchMultiplier: 1.25));
		model.SetRow(2, new(PitchMultiplier: 1.5));
		model.SetRow(3, new(PitchMultiplier: 1.75));
		Refresh(editor);
		var selection = Field<InstrumentToneGridSelection>(editor, "_selection");
		var rows = Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		selection.BeginAt(new(3, 2), rows);
		selection.Extend(new(1, 5), rows);
		ComboBox source = RowCell<ComboBox>(editor, 2, 2);
		object choice = source.ItemsSource!.Cast<object>().Single(x =>
			(ObjectId?)x.GetType().GetProperty("Id")!.GetValue(x) == sourceId);
		uint before = workspace.Document.AudioRevision;
		source.SelectedItem = choice;

		workspace.Document.AudioRevision.Should().Be(before + 1);
		instrument.ToneSpecifications.Should().HaveCount(3);
		model.Rows.Single(row => row.Index == 1).Cells.PitchMultiplier.Should().Be(1.25);
		model.Rows.Single(row => row.Index == 2).Cells.PitchMultiplier.Should().Be(1.5);
		model.Rows.Single(row => row.Index == 3).Cells.PitchMultiplier.Should().Be(1.75);
		foreach (int i in new[] { 1, 2, 3 })
			model.Rows.Single(r => r.Index == i).Cells.SourceId.Should().Be(sourceId);
		model.Rows.Single(r => r.Index == 0).Cells.SourceId.Should().BeNull();
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(3, 2));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(1, 5));
	}

	[AvaloniaTest]
	public void EditingColumnOutsideRectangleAffectsOnlyFocusedRow()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Selective fields");
		ObjectId source = AddSource(workspace);
		ObjectId envelope = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new NodeEnvelopeDefinition(envelope, "Volume"));
		InstrumentEditorControl editor = new(workspace, instrument,
			() => { }, _ => { });
		var model = Field<InstrumentToneGridModel>(editor, "_model");
		model.SetRow(1, new(SourceId: source));
		model.SetRow(2, new(SourceId: source));
		Refresh(editor);
		var selection = Field<InstrumentToneGridSelection>(editor, "_selection");
		var rows = Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		selection.BeginAt(new(2, 2), rows);
		selection.Extend(new(1, 5), rows);
		uint before = workspace.Document.AudioRevision;

		ComboBox nna = RowCell<StackPanel>(editor, 2, 5)
			.Children.OfType<ComboBox>().Single();
		nna.SelectedItem = InstrumentNnaAction.Off;
		workspace.Document.AudioRevision.Should().Be(before + 1);
		model.Rows.Single(r => r.Index == 1).Cells.Nna.Action
			.Should().Be(InstrumentNnaAction.Off);
		model.Rows.Single(r => r.Index == 2).Cells.Nna.Action
			.Should().Be(InstrumentNnaAction.Off);

		// Volume envelope column 6 lies outside the rectangle. A real
		// edit changes only its own row, even though the stack still has
		// a multi-row selection over NNA/Source/Pitch columns.
		ComboBox volume = RowCell<ComboBox>(editor, 2, 6);
		object option = volume.ItemsSource!.Cast<object>().Single(x =>
			(ObjectId?)x.GetType().GetProperty("Id")!.GetValue(x) == envelope);
		volume.SelectedItem = option;
		workspace.Document.AudioRevision.Should().Be(before + 2);
		model.Rows.Single(r => r.Index == 2).Cells.VolumeEnvelopeId.Should().Be(envelope);
		model.Rows.Single(r => r.Index == 1).Cells.VolumeEnvelopeId.Should().BeNull();
	}

	[AvaloniaTest]
	public void PitchEntryUsesDivisionUnitsAndWritesOneConstantValueNotRamp()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Forty-eight", divisions: 48);
		ObjectId source = AddSource(workspace);
		InstrumentEditorControl editor = new(workspace, instrument,
			() => { }, _ => { });
		InstrumentToneGridModel model = Field<InstrumentToneGridModel>(editor, "_model");
		model.SetRow(10, new(SourceId: source, PitchMultiplier: 1.3));
		model.SetRow(11, new(SourceId: source, PitchMultiplier: 0.8));
		Refresh(editor);
		var selection = Field<InstrumentToneGridSelection>(editor, "_selection");
		var rows = Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		selection.BeginAt(new(11, 3), rows);
		selection.Extend(new(10, 5), rows);
		TextBox pitch = RowCell<TextBox>(editor, 11, 3);
		uint before = workspace.Document.AudioRevision;

		pitch.Text = "5";
		pitch.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
		workspace.Document.AudioRevision.Should().Be(before + 1);
		double expected = Math.Pow(2, 5.0 / 48);
		model.Rows.Single(r => r.Index == 10).Cells.PitchMultiplier
			.Should().BeApproximately(expected, 1e-14);
		model.Rows.Single(r => r.Index == 11).Cells.PitchMultiplier
			.Should().BeApproximately(expected, 1e-14);
		InstrumentToneNoteNotation.FormatDisplayPitch(expected, 48,
			CultureInfo.InvariantCulture).Should().Be("5");
	}

	[AvaloniaTest]
	public void InvalidMultiRowPitchRejectsWithoutClobberingAnyCell()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Rollback");
		ObjectId source = AddSource(workspace);
		InstrumentEditorControl editor = new(workspace, instrument,
			() => { }, _ => { });
		InstrumentToneGridModel model = Field<InstrumentToneGridModel>(editor, "_model");
		model.SetRow(1, new(SourceId: source, PitchMultiplier: 1.3));
		model.SetRow(2, new(SourceId: source, PitchMultiplier: 1.7));
		Refresh(editor);
		var selection = Field<InstrumentToneGridSelection>(editor, "_selection");
		var rows = Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		selection.BeginAt(new(2, 3), rows);
		selection.Extend(new(1, 3), rows);
		uint before = workspace.Document.AudioRevision;
		TextBox pitch = RowCell<TextBox>(editor, 2, 3);
		pitch.Text = "999999";
		pitch.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

		workspace.Document.AudioRevision.Should().Be(before);
		model.Rows.Single(r => r.Index == 1).Cells.PitchMultiplier.Should().Be(1.3);
		model.Rows.Single(r => r.Index == 2).Cells.PitchMultiplier.Should().Be(1.7);
		Field<TextBlock>(editor, "_message").Text.Should().NotBeNullOrEmpty();
	}

	[AvaloniaTest]
	public void SinglePitchDisplayDoesNotAlterExactExistingMultiplierOnFocus()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Exact", divisions: 48);
		ObjectId source = AddSource(workspace);
		double exact = Math.Pow(2, 5.0 / 48);
		InstrumentToneGridModel before = new(workspace, instrument);
		before.SetRow(4, new(SourceId: source, PitchMultiplier: exact));
		InstrumentEditorControl editor = new(workspace, instrument,
			() => { }, _ => { });
		TextBox input = RowCell<TextBox>(editor, 4, 3);
		input.Text.Should().Be("5");
		uint revision = workspace.Document.AudioRevision;
		input.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
		workspace.Document.AudioRevision.Should().Be(revision);
		instrument.ToneSpecifications[instrument.ToneTable[4]]
			.PitchMultiplier.Should().Be(exact);
	}

	private static T RowCell<T>(InstrumentEditorControl editor,
		int toneIndex, int column) where T : Control
	{
		ListBox list = Field<ListBox>(editor, "_rows");
		var row = list.ItemsSource!.Cast<InstrumentToneGridRow>().Single(r =>
			r.Index == toneIndex);
		Grid grid = (Grid)((Border)list.ItemTemplate!.Build(row)!).Child!;
		return (T)((Border)grid.Children.Single(control =>
			Grid.GetColumn(control) == column)).Child!;
	}

	private static void Refresh(InstrumentEditorControl editor)
		=> typeof(InstrumentEditorControl).GetMethod(
			"RefreshRows", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, new object?[] { null });

	private static T Field<T>(InstrumentEditorControl editor, string name)
		=> (T)typeof(InstrumentEditorControl).GetField(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static ObjectId AddSource(DocumentWorkspace workspace)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new DataPatternDefinition(id, "Source"));
		return id;
	}
}
