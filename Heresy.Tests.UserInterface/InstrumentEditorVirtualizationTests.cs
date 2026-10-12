using System;
using System.Reflection;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Layout;
using AwesomeAssertions;

using Heresy.Core.Instruments;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentEditorVirtualizationTests
{
	[Test]
	public void ToneGridHasTenColumnsIncludingIndependentFilterSelection()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Ten");
		InstrumentEditorControl editor = new(
			workspace, instrument, () => { }, _ => { });
		int[] widths = (int[])typeof(InstrumentEditorControl).GetField(
			"ColumnWidths", BindingFlags.Static | BindingFlags.NonPublic)!
			.GetValue(null)!;
		widths.Should().HaveCount(10);
		// Exercise the actual projected-row data template: the added cell
		// contains a Filter selector and adjacent per-instance editor button.
		ListBox rows = (ListBox)typeof(InstrumentEditorControl).GetField(
			"_rows", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;
		Border border = (Border)rows.ItemTemplate!.Build(
			new InstrumentToneGridModel(workspace, instrument).Rows[^1])!;
		Grid row = (Grid)border.Child!;
		row.ColumnDefinitions.Should().HaveCount(10);
		Control filterCell = row.Children.Single(control => Grid.GetColumn(control) == 9);
		filterCell.Should().BeOfType<StackPanel>();
		((StackPanel)filterCell).Children.OfType<ComboBox>().Should().ContainSingle();
		((StackPanel)filterCell).Children.OfType<Button>().Should().ContainSingle();
	}

	[Test]
	public void RecycledNullItemDoesNotCrashOrPreventRenderingSubsequentRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		InstrumentEditorControl editor = new(
			workspace, instrument, () => { }, _ => { });

		// Exercise the actual FuncDataTemplate installed on the ListBox.
		// Avalonia can call it with null when a recycled item container's
		// ContentPresenter clears its content during scrolling.
		FieldInfo field = typeof(InstrumentEditorControl).GetField(
			"_rows", BindingFlags.Instance | BindingFlags.NonPublic)!;
		ListBox rows = (ListBox)field.GetValue(editor)!;
		Action recycle = () => { _ = rows.ItemTemplate!.Build(null); };
		recycle.Should().NotThrow();
		rows.ItemTemplate!.Build(null).Should().BeNull();

		var projected = new InstrumentToneGridModel(workspace, instrument).Rows;
		rows.ItemTemplate.Build(projected[0]).Should().BeOfType<Border>();
		rows.ItemTemplate.Build(projected[^1]).Should().BeOfType<Border>();
	}
}
