using System;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternStatusViewportTests
{
	[AvaloniaTest]
	public void StatusViewportMeasuresTwoAndQuarterActualTextLinesIncludingThemeAndFontChanges()
	{
		(Window owner, PatternEditorControl editor) =
			OpenEditor(width: 1050, height: 780);
		ScrollViewer scroll = Field<ScrollViewer>(editor, "_statusScroll");
		TextBlock diagnostic = Field<TextBlock>(editor, "_message");
		DockPanel root = (DockPanel)editor.Content!;
		Border border = root.Children.OfType<Border>()
			.Single(b => ReferenceEquals(b.Child, scroll));

		double firstFontLine = SingleLineHeight(diagnostic);
		scroll.Height.Should().BeApproximately(firstFontLine * 2.25, 0.01);
		scroll.Bounds.Height.Should().BeApproximately(scroll.Height, 0.01);
		border.Bounds.Height.Should().BeApproximately(
			scroll.Height + border.Padding.Top + border.Padding.Bottom, 0.01);
		scroll.VerticalScrollBarVisibility.Should().Be(ScrollBarVisibility.Auto);
		scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
		scroll.AllowAutoHide.Should().BeFalse();

		diagnostic.FontSize = 21;
		owner.UpdateLayout();
		double secondFontLine = SingleLineHeight(diagnostic);
		secondFontLine.Should().BeGreaterThan(firstFontLine);
		scroll.Height.Should().BeApproximately(secondFontLine * 2.25, 0.01);
		scroll.Bounds.Height.Should().BeApproximately(scroll.Height, 0.01);
		border.Bounds.Height.Should().BeApproximately(
			scroll.Height + border.Padding.Top + border.Padding.Bottom, 0.01);
		owner.Close();
	}

	[AvaloniaTest]
	public void ShortAndLongDiagnosticsAutomaticallyToggleStatusOverflowWithoutResizingGrid()
	{
		(Window owner, PatternEditorControl editor) =
			OpenEditor(width: 1040, height: 780);
		ScrollViewer status = Field<ScrollViewer>(editor, "_statusScroll");
		ScrollViewer grid = Field<ScrollViewer>(editor, "_scroll");
		TextBlock diagnostic = Field<TextBlock>(editor, "_message");
		TextBlock hint = Field<TextBlock>(editor, "_effectHint");
		PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
		uint revision = Workspace(editor).Document.AudioRevision;

		diagnostic.Text = "Ready.";
		cursor.SetPosition(0, 0, PatternCellField.Note);
		RefreshHint(editor);
		owner.UpdateLayout();
		hint.IsVisible.Should().BeFalse();
		status.ScrollBarMaximum.Y.Should().Be(0);
		status.Extent.Height.Should().BeLessThanOrEqualTo(
			status.Viewport.Height + 0.01);
		double statusHeight = status.Bounds.Height;
		double gridHeight = grid.Viewport.Height;

		diagnostic.Text = string.Join(" ", Enumerable.Repeat(
			"A multiline diagnostic that needs a scrollbar.", 75));
		cursor.SetPosition(0, 0, PatternCellField.EffectCommand);
		RefreshHint(editor);
		owner.UpdateLayout();
		hint.IsVisible.Should().BeTrue();
		status.ScrollBarMaximum.Y.Should().BeGreaterThan(0);
		status.Extent.Height.Should().BeGreaterThan(status.Viewport.Height);
		status.Bounds.Height.Should().BeApproximately(statusHeight, 0.01);
		grid.Viewport.Height.Should().BeApproximately(gridHeight, 0.01);
		status.Offset = new Vector(0, 12);
		owner.UpdateLayout();
		status.Offset.Y.Should().BeGreaterThan(0);
		diagnostic.Text.Should().Contain("multiline diagnostic");
		hint.Text.Should().Contain("Type a tracker command");

		diagnostic.Text = "Ready.";
		cursor.SetPosition(0, 0, PatternCellField.Note);
		RefreshHint(editor);
		owner.UpdateLayout();
		hint.IsVisible.Should().BeFalse();
		status.ScrollBarMaximum.Y.Should().Be(0);
		status.Bounds.Height.Should().BeApproximately(statusHeight, 0.01);
		grid.Viewport.Height.Should().BeApproximately(gridHeight, 0.01);
		Workspace(editor).Document.AudioRevision.Should().Be(revision);
		owner.Close();
	}

	[AvaloniaTest]
	public void NarrowLayoutWrapsBothStatusLinesButKeepsSameViewportHeight()
	{
		(Window owner, PatternEditorControl editor) =
			OpenEditor(width: 960, height: 720);
		ScrollViewer scroll = Field<ScrollViewer>(editor, "_statusScroll");
		TextBlock diagnostic = Field<TextBlock>(editor, "_message");
		TextBlock hint = Field<TextBlock>(editor, "_effectHint");
		PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
		double fixedHeight = scroll.Height;
		diagnostic.Text = "Error that must remain visible when the effect hint appears.";
		cursor.SetPosition(0, 0, PatternCellField.EffectCommand);
		RefreshHint(editor);
		owner.UpdateLayout();
		hint.IsVisible.Should().BeTrue();
		owner.Width = 450;
		owner.UpdateLayout();
		scroll.Height.Should().BeApproximately(fixedHeight, 0.01);
		scroll.Bounds.Height.Should().BeApproximately(fixedHeight, 0.01);
		scroll.ScrollBarMaximum.Y.Should().BeGreaterThan(0);
		diagnostic.Text.Should().StartWith("Error");
		hint.Text.Should().Contain("Type a tracker command");
		owner.Close();
	}

	private static double SingleLineHeight(TextBlock reference)
	{
		TextBlock measurement = new()
		{
			Text = "Ag",
			TextWrapping = TextWrapping.NoWrap,
			FontFamily = reference.FontFamily,
			FontSize = reference.FontSize,
			FontWeight = reference.FontWeight,
			FontStyle = reference.FontStyle,
			LineHeight = reference.LineHeight,
			LineSpacing = reference.LineSpacing,
		};
		measurement.Measure(new Size(
			double.PositiveInfinity, double.PositiveInfinity));
		return measurement.DesiredSize.Height;
	}

	private static (Window Window, PatternEditorControl Editor) OpenEditor(
		double width, double height)
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Status", rowCount: 32, channelCount: 2);
		Window owner = new() { Width = width, Height = height };
		PatternEditorControl editor = new(
			owner, workspace, pattern, () => { }, _ => { });
		owner.Content = editor;
		owner.Show();
		owner.UpdateLayout();
		return (owner, editor);
	}

	private static T Field<T>(PatternEditorControl editor, string name)
		=> (T)typeof(PatternEditorControl).GetField(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static DocumentWorkspace Workspace(PatternEditorControl editor)
		=> Field<DocumentWorkspace>(editor, "_workspace");

	private static void RefreshHint(PatternEditorControl editor)
		=> typeof(PatternEditorControl).GetMethod(
			"RefreshCursorVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, null);
}
