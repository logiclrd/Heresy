using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEditorToolbarTests
{
	[AvaloniaTest]
	public void BackButtonIsOutsideAutoScrollingToolbarContainingTitleThroughApplyLayout()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Verse", rowCount: 2, channelCount: 1);
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		DockPanel root = (DockPanel)editor.Content!;
		DockPanel header = (DockPanel)root.Children[0];

		Button back = header.Children[0].Should().BeOfType<Button>().Which;
		back.Content.Should().Be("← Document");
		ScrollViewer scroll = header.Children[1]
			.Should().BeOfType<ScrollViewer>().Which;
		scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Auto);
		scroll.VerticalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
		StackPanel content = scroll.Content.Should().BeOfType<StackPanel>().Which;
		content.Children[0].Should().BeOfType<Grid>();
		StackPanel controls = content.Children[1].Should()
			.BeOfType<StackPanel>().Which;
		controls.Children.Last().Should().BeOfType<Button>()
			.Which.Content.Should().Be("Apply layout");
	}

	[AvaloniaTest]
	public void ToolbarAlwaysReservesVerticalScrollbarClearance()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Verse", rowCount: 2, channelCount: 1);
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		DockPanel root = (DockPanel)editor.Content!;
		DockPanel header = (DockPanel)root.Children[0];
		Button back = (Button)header.Children[0];
		ScrollViewer scroll = (ScrollViewer)header.Children[1];
		StackPanel toolbar = (StackPanel)scroll.Content!;

		// This clearance is permanent, not toggled with Auto scrollbar
		// visibility: narrow windows must not cover the control bottoms,
		// and resizing must not change the toolbar's requested height.
		scroll.MinHeight.Should().BeGreaterThan(59);
		toolbar.Margin.Bottom.Should().BeGreaterThan(19);
		toolbar.VerticalAlignment.Should().Be(Avalonia.Layout.VerticalAlignment.Top);
		back.VerticalAlignment.Should().Be(Avalonia.Layout.VerticalAlignment.Top);
		scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Auto);
	}

	[AvaloniaTest]
	public void InlineTitleRenameUpdatesTreeAndDocumentButNotAudio()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Verse", rowCount: 2, channelCount: 1);
		string? notification = null;
		PatternEditorControl editor = new(new Window(), workspace, pattern,
			() => { }, message => notification = message);
		uint revision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		Invoke(editor, "BeginTitleEdit");
		TextBox input = Field<TextBox>(editor, "_titleEditor");
		input.IsVisible.Should().BeTrue();
		input.Text.Should().Be("Verse");
		input.Text = "  New Verse  ";
		Invoke(editor, "CloseTitleEdit", true);

		pattern.Name.Should().Be("New Verse");
		workspace.Document.GetSectionRoot(SongTreeSection.Patterns)
			.Children.OfType<SongTreeObject>().Single().Name.Should().Be("New Verse");
		workspace.Document.DocumentRevision.Should().Be(revision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
		Field<TextBlock>(editor, "_title").Text.Should().Be("New Verse");
		input.IsVisible.Should().BeFalse();
		notification.Should().Contain("Renamed pattern");
	}

	[AvaloniaTest]
	public void EscapeCancellationAndBlankNameLeavePatternUntouched()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Original", rowCount: 2, channelCount: 1);
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		uint revision = workspace.Document.DocumentRevision;

		Invoke(editor, "BeginTitleEdit");
		TextBox input = Field<TextBox>(editor, "_titleEditor");
		input.Text = "Discarded";
		Invoke(editor, "CloseTitleEdit", false);
		// A later LostFocus cannot commit an already-cancelled edit.
		Invoke(editor, "CloseTitleEdit", true);
		pattern.Name.Should().Be("Original");
		input.IsVisible.Should().BeFalse();

		Invoke(editor, "BeginTitleEdit");
		input.Text = "  ";
		Invoke(editor, "CloseTitleEdit", true);
		pattern.Name.Should().Be("Original");
		workspace.Document.DocumentRevision.Should().Be(revision);
		Field<TextBlock>(editor, "_title").Text.Should().Be("Original");
	}

	private static T Field<T>(PatternEditorControl editor, string name)
		=> (T)typeof(PatternEditorControl).GetField(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static void Invoke(PatternEditorControl editor,
		string name, params object[] arguments)
		=> typeof(PatternEditorControl).GetMethod(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, arguments);
}
