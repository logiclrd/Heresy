using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;
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
	public void MaskMiniatureAlwaysShowsTheSameCellTextAndIndependentFieldOpacities()
	{
		PatternEditMaskStatusCell miniature = new();
		StackPanel content = (StackPanel)miniature.Child!;
		TextBlock[] segments =
		[
			miniature.NoteSegment, miniature.SourceSegment,
			miniature.VolumeSegment, miniature.EffectSegment,
		];
		segments.Should().OnlyContain(x => content.Children.Contains(x));
		string.Join(" ", segments.Select(x => x.Text)).Should().Be("C-5 src 48 ...");
		content.Orientation.Should().Be(Avalonia.Layout.Orientation.Horizontal);
		content.Spacing.Should().BeGreaterThan(0);
		for (int bits = 0; bits < 8; bits++)
		{
			PatternEditMask mask = (PatternEditMask)bits;
			miniature.SetMask(mask);
			segments[0].Opacity.Should().Be(
				(mask & PatternEditMask.Note) != 0 ? 1.0 : 0.6);
			segments[1].Opacity.Should().Be(
				(mask & PatternEditMask.Source) != 0 ? 1.0 : 0.6);
			segments[2].Opacity.Should().Be(
				(mask & PatternEditMask.Volume) != 0 ? 1.0 : 0.6);
			segments[3].Opacity.Should().Be(0.6);
			string.Join(" ", segments.Select(x => x.Text))
				.Should().Be("C-5 src 48 ...");
		}
	}

	[AvaloniaTest]
	public void MaskMiniatureUsesExactSourceHueAndReservesAtLeastOneEnOnRight()
	{
		PatternEditMaskStatusCell miniature = new();
		uint argb = SongSourceColour.FromHue(280);
		SolidColorBrush brush = miniature.Background.Should()
			.BeOfType<SolidColorBrush>().Which;
		brush.Color.Should().Be(Color.FromArgb(
			(byte)(argb >> 24), (byte)(argb >> 16),
			(byte)(argb >> 8), (byte)argb));
		miniature.Margin.Right.Should().BeGreaterThanOrEqualTo(
			miniature.NoteSegment.FontSize,
			"the right gutter must exceed even a full 14-DIP em, not merely one en");
		miniature.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		((StackPanel)miniature.Child!).DesiredSize.Width.Should().BeGreaterThan(0);
		miniature.DesiredSize.Width.Should().BeGreaterThanOrEqualTo(
			((StackPanel)miniature.Child!).DesiredSize.Width + miniature.Margin.Right,
			"the right gutter must be included in the measured width for narrow toolbars");

		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Verse", rowCount: 2, channelCount: 1);
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		PatternEditMaskStatusCell actual = Field<PatternEditMaskStatusCell>(
			editor, "_editMaskDisplay");
		actual.Should().NotBeNull();
		DockPanel root = (DockPanel)editor.Content!;
		DockPanel header = (DockPanel)root.Children[0];
		ScrollViewer scroll = (ScrollViewer)header.Children[1];
		StackPanel toolbar = (StackPanel)scroll.Content!;
		StackPanel controls = (StackPanel)toolbar.Children[1];
		int index = controls.Children.IndexOf(actual);
		index.Should().BeGreaterThan(0);
		((TextBlock)controls.Children[index - 1]).Text.Should().Be("Mask");
		((TextBlock)controls.Children[index + 1]).Text.Should().Be("Edit Vol");
		scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Auto);
		actual.Margin.Right.Should().BeGreaterThanOrEqualTo(
			actual.NoteSegment.FontSize);
		uint revision = workspace.Document.AudioRevision;
		actual.SetMask(PatternEditMask.None);
		workspace.Document.AudioRevision.Should().Be(revision,
			"the visual mask example never mutates the song");
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
