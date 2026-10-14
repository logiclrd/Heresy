using System;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Layout;

using AwesomeAssertions;

using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneRowDensityTests
{
	[Test]
	public void RowTemplateHasNoFixedMinimumOrVerticalRowMarginsAndProtectsEachCell()
	{
		var (editor, _) = EditorWithTallOptionalFields();
		Border normal = BuildRow(editor, 0);
		Border entry = BuildEntry(editor);
		foreach (Border row in new[] { normal, entry })
		{
			Grid grid = (Grid)row.Child!;
			grid.MinHeight.Should().Be(0);
			grid.Margin.Top.Should().Be(0);
			grid.Margin.Bottom.Should().Be(0);
			grid.Children.Should().HaveCount(11);
			foreach (Border cell in grid.Children.Cast<Border>())
			{
				cell.BorderThickness.Top.Should().Be(1);
				cell.BorderThickness.Bottom.Should().Be(1);
				cell.Padding.Top.Should().BeGreaterThanOrEqualTo(1);
				cell.Padding.Bottom.Should().BeGreaterThanOrEqualTo(1);
				foreach (Control control in NestedInputs(cell))
				{
					control.MinHeight.Should().Be(0);
					double.IsNaN(control.Height).Should().BeTrue(
						"compact inputs must grow with text and theme templates, not be clipped to a fixed height");
					if (control is TextBox tb)
					{
						tb.Padding.Top.Should().Be(1);
						tb.Padding.Bottom.Should().Be(1);
					}
					else if (control is ComboBox cb)
					{
						cb.Padding.Top.Should().Be(1);
						cb.Padding.Bottom.Should().Be(1);
					}
					else if (control is Button button)
					{
						button.Padding.Top.Should().Be(1);
						button.Padding.Bottom.Should().Be(1);
					}
				}
			}
		}
		// Header layout is also content-driven, not held to the previous 32px.
		Grid headers = (Grid)typeof(InstrumentEditorControl)
			.GetMethod("NewColumns", BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, null)!;
		headers.MinHeight.Should().Be(0);
	}

	[AvaloniaTest]
	public void ActualFluentLayoutFitsExpandedNnaAndFilterControlsWithoutClipping()
	{
		var (editor, workspace) = EditorWithTallOptionalFields();
		Border regular = BuildRow(editor, 0);
		Border entry = BuildEntry(editor);
		Window host = new()
		{
			Width = 1740,
			Height = 280,
			Content = new StackPanel
			{
				Orientation = Orientation.Vertical,
				Children = { regular, entry },
			},
		};
		uint revision = workspace.Document.AudioRevision;
		host.Show();
		try
		{
			foreach (Border row in new[] { regular, entry })
			{
				Grid grid = (Grid)row.Child!;
				grid.Bounds.Height.Should().BeGreaterThan(0);
				foreach (Border cell in grid.Children.Cast<Border>())
				{
					cell.Bounds.Height.Should().BeGreaterThanOrEqualTo(
						cell.Child?.Bounds.Height + 4 ?? 4,
						"selection border and 1px padding must fit outside the actual control template");
					if (cell.Child is Control content)
					{
						content.Bounds.Y.Should().BeGreaterThanOrEqualTo(2);
						content.Bounds.Bottom.Should().BeLessThanOrEqualTo(
							cell.Bounds.Height - 2 + 0.01);
					}
				}
			}
			// The old row could never be shorter than 32px grid
			// minimum + 2px top/2px bottom margins + 1px separator.
			// Our current Fluent theme must demonstrate a real density
			// improvement, without capping production control heights.
			regular.Bounds.Height.Should().BeLessThan(37);
			entry.Bounds.Height.Should().BeLessThan(37);

			// Fade activates a second text editor in one column, and the
			// filter's actual selector and edit button remain side-by-side.
			Grid regularGrid = (Grid)regular.Child!;
			StackPanel nna = (StackPanel)Column(regularGrid, 5).Child!;
			nna.Children.OfType<TextBox>().Single().IsVisible.Should().BeTrue();
			StackPanel filter = (StackPanel)Column(regularGrid, 10).Child!;
			filter.Children.OfType<ComboBox>().Should().ContainSingle();
			filter.Children.OfType<Button>().Should().ContainSingle();
			nna.Bounds.Width.Should().BeLessThanOrEqualTo(
				Column(regularGrid, 5).Bounds.Width + 0.01);
			filter.Bounds.Width.Should().BeLessThanOrEqualTo(
				Column(regularGrid, 10).Bounds.Width + 0.01);

			// Exercise the real themed popup and keyboard focus path
			// without imposing a fixed cell height that could clip either.
			ComboBox source = (ComboBox)Column(regularGrid, 2).Child!;
			source.IsDropDownOpen = true;
			source.IsDropDownOpen.Should().BeTrue();
			source.IsDropDownOpen = false;
			TextBox pitch = (TextBox)Column(regularGrid, 3).Child!;
			pitch.Focus().Should().BeTrue();
			pitch.IsFocused.Should().BeTrue();
			InstrumentToneGridSelection selection =
				Field<InstrumentToneGridSelection>(editor, "_selection");
			selection.Focused.Should().Be(
				new InstrumentToneGridSelection.Cell(0, 3));
			Column(regularGrid, 3).BorderBrush.Should().Be(
				Avalonia.Media.Brushes.DeepSkyBlue);

			TestContext.Out.WriteLine(
				$"Compacted instrument rows (Fluent/headless): regular={regular.Bounds.Height:F2}px, " +
				$"entry={entry.Bounds.Height:F2}px, NNA={nna.Bounds.Height:F2}px, filter={filter.Bounds.Height:F2}px");
			workspace.Document.AudioRevision.Should().Be(revision);
		}
		finally
		{
			host.Close();
		}
	}

	[AvaloniaTest]
	public void ActualEditorVirtualScrollRetainsCellSelectionAndCanOpenDropdown()
	{
		var (editor, workspace) = EditorWithTallOptionalFields();
		Window window = new() { Content = editor, Width = 1740, Height = 490 };
		ListBox rows = Field<ListBox>(editor, "_rows");
		InstrumentToneGridSelection selection =
			Field<InstrumentToneGridSelection>(editor, "_selection");
		InstrumentToneGridRow[] projected =
			Field<InstrumentToneGridRow[]>(editor, "_projectedRows");
		selection.BeginAt(new(84, 2), projected);
		selection.Extend(new(80, 10), projected);
		uint audioRevision = workspace.Document.AudioRevision;
		window.Show();
		try
		{
			rows.ScrollIntoView(projected.Length - 1);
			rows.ScrollIntoView(0);
			selection.Anchor.Should().Be(
				new InstrumentToneGridSelection.Row(84));
			selection.Extent.Should().Be(
				new InstrumentToneGridSelection.Row(80));
			selection.Contains(new(82, 0), projected).Should().BeTrue();
			selection.Contains(new(82, 10), projected).Should().BeTrue();
			rows.ItemTemplate!.Build(null).Should().BeNull();
			Border regular = BuildRow(editor, 0);
			ComboBox combo = (ComboBox)Column((Grid)regular.Child!, 2).Child!;
			// The actual dropdown must remain a standard ComboBox: no
			// custom clipped popup or capture of arrow keys while open.
			combo.ItemsSource.Should().NotBeNull();
			combo.MinHeight.Should().Be(0);
			window.IsVisible.Should().BeTrue();
			workspace.Document.AudioRevision.Should().Be(audioRevision);
		}
		finally
		{
			window.Close();
		}
	}

	private static (InstrumentEditorControl, DocumentWorkspace) EditorWithTallOptionalFields()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Dense");
		ObjectId sound = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new DataPatternDefinition(sound, "Source"));
		ObjectId filterId = workspace.Document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Filter");
		workspace.Document.Add(definition);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(0, new(SourceId: sound,
			Nna: InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(31)),
			FilterConfiguration: new FilterInstance(definition)));
		return (new InstrumentEditorControl(workspace, instrument,
			() => { }, _ => { }), workspace);
	}

	private static Border BuildEntry(InstrumentEditorControl editor)
	{
		ListBox rows = Field<ListBox>(editor, "_rows");
		return (Border)rows.ItemTemplate!.Build(
			rows.ItemsSource!.Cast<InstrumentToneGridRow>().Single(r => r.IsEntry))!;
	}

	private static Border BuildRow(InstrumentEditorControl editor, int index)
	{
		ListBox rows = Field<ListBox>(editor, "_rows");
		return (Border)rows.ItemTemplate!.Build(
			rows.ItemsSource!.Cast<InstrumentToneGridRow>().Single(r => r.Index == index))!;
	}

	private static Border Column(Grid grid, int column)
		=> (Border)grid.Children.Single(c => Grid.GetColumn(c) == column);

	private static Control[] NestedInputs(Border surface)
	{
		if (surface.Child is TextBox or ComboBox or Button)
			return [(Control)surface.Child];
		if (surface.Child is StackPanel panel)
			return panel.Children.OfType<Control>()
				.Where(c => c is TextBox or ComboBox or Button).ToArray();
		return [];
	}

	private static T Field<T>(InstrumentEditorControl editor, string name)
		=> (T)typeof(InstrumentEditorControl).GetField(name,
			BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

}
