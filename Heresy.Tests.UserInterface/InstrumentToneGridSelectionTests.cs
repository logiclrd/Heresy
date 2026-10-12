using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using AwesomeAssertions;

using Heresy.Core.Instruments;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneGridSelectionTests
{
	[Test]
	public void RectangularExtensionUsesDescendingVisualToneOrderAndOnlyIncludedColumns()
	{
		var (rows, selection) = NewSelection();
		selection.BeginAt(new(84, 3), rows);
		selection.Extend(new(81, 6), rows);

		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(84, 3));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(81, 6));
		foreach (int index in new[] { 84, 83, 82, 81 })
			foreach (int column in new[] { 3, 4, 5, 6 })
				selection.Contains(new(index, column), rows).Should().BeTrue();

		selection.Contains(new(80, 4), rows).Should().BeFalse();
		selection.Contains(new(83, 2), rows).Should().BeFalse();
		selection.Contains(new(83, 7), rows).Should().BeFalse();
		selection.Contains(new(85, 4), rows).Should().BeFalse();
	}

	[Test]
	public void FocusInsidePreservesExistingRectangleOutsideFocusResetsIt()
	{
		var (rows, selection) = NewSelection();
		selection.BeginAt(new(82, 3), rows);
		selection.Extend(new(79, 7), rows);

		selection.Focus(new(81, 5), rows);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(82, 3));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(79, 7));
		selection.Focused.Should().Be(new InstrumentToneGridSelection.Cell(81, 5));
		selection.Contains(new(79, 7), rows).Should().BeTrue();

		selection.Focus(new(81, 8), rows);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(81, 8));
		selection.Extent.Should().Be(selection.Anchor);
		selection.Focused.Should().Be(selection.Anchor);
		selection.Contains(new(82, 3), rows).Should().BeFalse();
	}

	[Test]
	public void ShiftClickExtendsFromOriginalAnchorAndCanReverseBounds()
	{
		var (rows, selection) = NewSelection();
		selection.BeginAt(new(81, 8), rows);
		selection.Extend(new(84, 2), rows);
		selection.Extend(new(79, 5), rows);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(81, 8));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(79, 5));
		selection.Contains(new(80, 7), rows).Should().BeTrue();
		selection.Contains(new(83, 7), rows).Should().BeFalse();
	}

	[Test]
	public void ArrowNeighborsFollowVisualOrderAndStopAtGridEdges()
	{
		var (rows, selection) = NewSelection();
		selection.BeginAt(new(84, 3), rows);
		selection.Neighbor(1, 0, rows).Should().Be(new InstrumentToneGridSelection.Cell(83, 3));
		selection.Neighbor(-1, 0, rows).Should().Be(new InstrumentToneGridSelection.Cell(null, 3));
		selection.Neighbor(0, -1, rows).Should().Be(new InstrumentToneGridSelection.Cell(84, 2));
		selection.Neighbor(0, 1, rows).Should().Be(new InstrumentToneGridSelection.Cell(84, 4));

		selection.BeginAt(new(null, 0), rows);
		selection.Neighbor(-1, 0, rows).Should().BeNull();
		selection.Neighbor(0, -1, rows).Should().BeNull();
		selection.BeginAt(new(0, 10), rows);
		selection.Neighbor(1, 0, rows).Should().BeNull();
		selection.Neighbor(0, 1, rows).Should().BeNull();
	}

	[Test]
	public void BoundaryShiftLeftEstablishesLeftCellAsBothAnchorAndExtent()
	{
		var (rows, selection) = NewSelection();
		selection.BeginAt(new(82, 4), rows);
		InstrumentToneGridSelection.Cell? left = selection.Neighbor(0, -1, rows);
		left.Should().NotBeNull();
		selection.BeginAt(left!.Value, rows);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(82, 3));
		selection.Extent.Should().Be(selection.Anchor);
		selection.Focused.Should().Be(selection.Anchor);
		selection.Extend(new(80, 4), rows);
		selection.Contains(new(81, 3), rows).Should().BeTrue();
		selection.Contains(new(82, 4), rows).Should().BeTrue();
	}

	[Test]
	public void ReprojectionRetainsIdentitiesAcrossInsertedExtraRowsAndClearsMissingRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Stable");
		InstrumentToneGridModel model = new(workspace, instrument);
		InstrumentToneGridRow[] first = model.Rows.ToArray();
		InstrumentToneGridSelection selection = new();
		selection.BeginAt(new(84, 2), first);
		selection.Extend(new(82, 8), first);
		model.SetRow(100, new InstrumentToneGridCells(PitchMultiplier: 1.5));
		InstrumentToneGridRow[] withExtra = model.Rows.ToArray();
		selection.Reconcile(withExtra);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(84, 2));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(82, 8));
		selection.Contains(new(83, 6), withExtra).Should().BeTrue();
		selection.Contains(new(100, 6), withExtra).Should().BeFalse();

		// When a previously selected extra row disappears, reuse the
		// surviving focused index, never the recycled visual row number.
		selection.BeginAt(new(100, 1), withExtra);
		selection.Extend(new(84, 3), withExtra);
		model.DeleteRow(100);
		InstrumentToneGridRow[] withoutExtra = model.Rows.ToArray();
		selection.Reconcile(withoutExtra);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(84, 3));
		selection.Extent.Should().Be(selection.Anchor);
	}

	[Test]
	public void EntryRowIsDistinctAndCanBeSelectedWithoutSongMutation()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Entry");
		InstrumentToneGridRow[] rows = new InstrumentToneGridModel(
			workspace, instrument).Rows.ToArray();
		InstrumentToneGridSelection selection = new();
		uint revision = workspace.Document.AudioRevision;
		selection.BeginAt(new(null, 0), rows);
		selection.Extend(new(84, 5), rows);
		selection.Contains(new(null, 4), rows).Should().BeTrue();
		selection.Contains(new(84, 5), rows).Should().BeTrue();
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[Test]
	public void VirtualizedRowTemplateRebuildUsesTheSameSemanticRectangle()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Projected");
		InstrumentEditorControl editor =
			new(workspace, instrument, () => { }, _ => { });
		ListBox rows = Field<ListBox>(editor, "_rows");
		InstrumentToneGridRow[] projected =
			Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		InstrumentToneGridSelection selection =
			Field<InstrumentToneGridSelection>(editor, "_selection");
		selection.BeginAt(new(84, 2), projected);
		selection.Extend(new(82, 5), projected);
		uint revision = workspace.Document.AudioRevision;

		Border first = (Border)rows.ItemTemplate!.Build(
			projected.Single(row => row.Index == 83))!;
		Grid firstGrid = (Grid)first.Child!;
		AssertSelection(firstGrid, 2, true);
		AssertSelection(firstGrid, 5, true);
		AssertSelection(firstGrid, 6, false);
		first = null!; // model remains; recycled UI is disposable

		Border rebuilt = (Border)rows.ItemTemplate.Build(
			projected.Single(row => row.Index == 83))!;
		Grid grid = (Grid)rebuilt.Child!;
		AssertSelection(grid, 3, true);
		AssertSelection(grid, 1, false);
		selection.Anchor.Should().Be(new InstrumentToneGridSelection.Cell(84, 2));
		selection.Extent.Should().Be(new InstrumentToneGridSelection.Cell(82, 5));
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	private static void AssertSelection(Grid grid, int column, bool expected)
	{
		Border cell = grid.Children.OfType<Border>().Single(
			item => Grid.GetColumn(item) == column);
		// A nontransparent background represents selection; every other
		// cell has an ordinary transparent selection surface.
		bool active = cell.Background is SolidColorBrush brush && brush.Color.A != 0;
		active.Should().Be(expected);
	}

	private static (InstrumentToneGridRow[], InstrumentToneGridSelection) NewSelection()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Selector");
		return (new InstrumentToneGridModel(workspace, instrument).Rows.ToArray(),
			new InstrumentToneGridSelection());
	}

	private static T Field<T>(InstrumentEditorControl editor, string name)
		=> (T)typeof(InstrumentEditorControl).GetField(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;
}
