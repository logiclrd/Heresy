using System;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternPageNavigationTests
{
	[TestCase(0, 0)]
	[TestCase(20, 0)]
	[TestCase(27.999, 0)]
	[TestCase(28, 1)]
	[TestCase(55.999, 1)]
	[TestCase(56, 2)]
	[TestCase(139.999, 4)]
	[TestCase(140, 5)]
	[TestCase(167.999, 5)]
	[TestCase(168, 6)]
	public void PageHeightCountsCompleteRowsIncludingSharedTopBorder(
		double height, int expected)
	{
		PatternPageNavigation.RowsPerPage(height, 28)
			.Should().Be(expected);
	}

	[Test]
	public void InvalidViewportIsNotMistakenForEntireDocumentPage()
	{
		PatternPageNavigation.RowsPerPage(double.NaN, 28)
			.Should().Be(0);
		PatternPageNavigation.RowsPerPage(double.PositiveInfinity, 28)
			.Should().Be(0);
		PatternPageNavigation.RowsPerPage(-100, 28)
			.Should().Be(0);
		Action invalid = () => PatternPageNavigation.RowsPerPage(100, 0);
		invalid.Should().Throw<ArgumentOutOfRangeException>();
		PatternPageNavigation.RowsPerPage(double.MaxValue, 28)
			.Should().Be(int.MaxValue);
	}

	[TestCase(false, 1, 2, 140)]
	[TestCase(true, 1, 2, 140)]
	[TestCase(false, 6, 3, 196)]
	[TestCase(true, 6, 3, 196)]
	[TestCase(false, 0, 0, 28)]
	[TestCase(true, 8, 0, 28)]
	[TestCase(false, 3, 0, 20)]
	[TestCase(true, 3, 0, 20)]
	public void PageCursorHasSameFinalPositionAsExactlyRepeatedArrowKeys(
		bool down, int startRow, int startChannel, double height)
	{
		var (context, _, _, _) = MakeSequence();
		PatternEffectCursor page = new(
			startRow, startChannel, PatternCellField.Octave);
		PatternEffectCursor arrows = new(
			startRow, startChannel, PatternCellField.Octave);
		int repetitions = PatternPageNavigation.RowsPerPage(height, 28);
		int returned = PatternPageNavigation.Move(
			context, page, height, 28, down);
		for (int n = 0; n < repetitions; n++)
		{
			if (down)
				PatternEditorContextCursor.MoveDown(context, arrows);
			else
				PatternEditorContextCursor.MoveUp(context, arrows);
		}

		returned.Should().Be(repetitions);
		page.Row.Should().Be(arrows.Row);
		page.Channel.Should().Be(arrows.Channel);
		page.Field.Should().Be(arrows.Field);
		page.IsExpanded.Should().Be(arrows.IsExpanded);
	}

	[Test]
	public void PageCrossingNarrowSequencePatternClampsChannelAtIntermediateRows()
	{
		var (context, _, _, _) = MakeSequence();
		PatternEffectCursor forward =
			new(1, 2, PatternCellField.EffectParameter);
		PatternPageNavigation.Move(
			context, forward, viewportHeight: 5 * 28,
			rowHeight: 28, down: true).Should().Be(5);
		forward.Row.Should().Be(6);
		forward.Channel.Should().Be(0);
		forward.Field.Should().Be(PatternCellField.EffectParameter);

		PatternEffectCursor reverse =
			new(6, 3, PatternCellField.Source);
		PatternPageNavigation.Move(
			context, reverse, 5 * 28, 28, down: false);
		reverse.Row.Should().Be(1);
		reverse.Channel.Should().Be(0);
		reverse.Field.Should().Be(PatternCellField.Source);
	}

	[Test]
	public void PageMotionCollapsesExpandedEffectLikeOrdinaryArrows()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Effects", rowCount: 12, channelCount: 2);
		PatternCell cell = pattern.Grid.GetOrCreateCell(3, 1);
		cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x21));
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x12));
		PatternEditorContext context =
			PatternEditorContext.ForPattern(workspace.Document, pattern);
		PatternEffectCursor cursor =
			new(3, 1, PatternCellField.EffectCommand);
		cursor.Expand(cell);
		cursor.IsExpanded.Should().BeTrue();

		PatternPageNavigation.Move(context, cursor, 4 * 28, 28, down: true);
		cursor.Row.Should().Be(7);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);
		cursor.IsExpanded.Should().BeFalse();

		cursor.SetPosition(11, 1, PatternCellField.EffectParameter);
		cursor.Expand(cell);
		PatternPageNavigation.Move(context, cursor, 28, 28, down: true);
		cursor.Row.Should().Be(11);
		cursor.IsExpanded.Should().BeFalse();
	}

	[Test]
	public void ScrollCalculationLeavesVisibleCursorUntouchedAndMovesOnlyAsNeeded()
	{
		PatternPageNavigation.OffsetToKeepVisible(150, 28, 100, 200, 1000)
			.Should().Be(100);
		PatternPageNavigation.OffsetToKeepVisible(99, 28, 100, 200, 1000)
			.Should().Be(99);
		PatternPageNavigation.OffsetToKeepVisible(290, 28, 100, 200, 1000)
			.Should().Be(118);
		PatternPageNavigation.OffsetToKeepVisible(150, 28, 100, 200, 1000)
			.Should().Be(100);
		PatternPageNavigation.OffsetToKeepVisible(150, 28, 100, 200, 220)
			.Should().Be(20);
		PatternPageNavigation.OffsetToKeepVisible(970, 28, 900, 200, 1000)
			.Should().Be(800);
		PatternPageNavigation.OffsetToKeepVisible(300, 28, 100, 0, 1000)
			.Should().Be(100);
		PatternPageNavigation.OffsetToKeepVisible(300, 70, 100, 30, 1000)
			.Should().Be(300);
	}

	[AvaloniaTest]
	public async Task PageKeyMovesFocusedGridCursorRatherThanPagingScrollViewer()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Visible rows", rowCount: 96, channelCount: 2);
		PatternEditorControl editor = new(
			new Window(), workspace,
			PatternEditorContext.ForPattern(workspace.Document, pattern),
			() => { }, _ => { });

		// Measure/arrange the real editor so the ScrollViewer reports the
		// usable grid viewport, not the window (which includes toolbars,
		// chord controls, status text, and possibly a horizontal scrollbar).
		editor.Measure(new Size(920, 880));
		editor.Arrange(new Rect(0, 0, 920, 880));
		ScrollViewer scroll = (ScrollViewer)typeof(PatternEditorControl)
			.GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;
		scroll.Viewport.Height.Should().BeGreaterThan(56);
		int pageRows =
			PatternPageNavigation.RowsPerPage(scroll.Viewport.Height, 28);
		pageRows.Should().BeGreaterThan(2);
		int first = pageRows + 2;
		PatternEffectCursor cursor = (PatternEffectCursor)typeof(PatternEditorControl)
			.GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;
		cursor.SetPosition(first, 1, PatternCellField.Octave);
		uint revision = workspace.Document.AudioRevision;

		KeyEventArgs down = await Press(editor, first, 1, Key.PageDown);
		down.Handled.Should().BeTrue();
		cursor.Row.Should().Be(first + pageRows);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Octave);
		scroll.Offset.Y.Should().BeGreaterThan(0,
			"Page Down must reveal an offscreen focused tracker row");

		KeyEventArgs up = await Press(editor, cursor.Row, 1, Key.PageUp);
		up.Handled.Should().BeTrue();
		cursor.Row.Should().Be(first);
		cursor.Channel.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Octave);
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[AvaloniaTest]
	public async Task ShiftPageExtendsSelectionWithoutChangingPitchOrAudio()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Page selection", rowCount: 70, channelCount: 1);
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		editor.Measure(new Size(1100, 820));
		editor.Arrange(new Rect(0, 0, 1100, 820));
		ScrollViewer scroll = (ScrollViewer)typeof(PatternEditorControl)
			.GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;
		int steps = PatternPageNavigation.RowsPerPage(scroll.Viewport.Height, 28);
		steps.Should().BeGreaterThan(0);
		PatternEffectCursor cursor = (PatternEffectCursor)typeof(PatternEditorControl)
			.GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;
		PatternSelectionState selection = (PatternSelectionState)typeof(PatternEditorControl)
			.GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;
		cursor.SetPosition(25, 0, PatternCellField.Source);
		uint revision = workspace.Document.AudioRevision;

		KeyEventArgs key = await Press(editor, 25, 0,
			Key.PageUp, KeyModifiers.Shift);
		key.Handled.Should().BeTrue();
		cursor.Row.Should().Be(Math.Max(0, 25 - steps));
		cursor.Field.Should().Be(PatternCellField.Source);
		selection.Region.Should().Be(
			new PatternSelectionRegion(Math.Max(0, 25 - steps), 0, 25, 0));
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	private static async Task<KeyEventArgs> Press(PatternEditorControl editor,
		int row, int channel, Key key, KeyModifiers modifiers = KeyModifiers.None)
	{
		KeyEventArgs e = new() { Key = key, KeyModifiers = modifiers };
		Task task = (Task)typeof(PatternEditorControl).GetMethod(
			"OnCellKeyDownAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, [row, channel, e])!;
		await task;
		return e;
	}

	private static (PatternEditorContext, DataPatternDefinition,
		DataPatternDefinition, DataPatternDefinition) MakeSequence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(workspace, "Wide", 3, 3);
		DataPatternDefinition narrow =
			PatternDocumentEditor.CreateDataPattern(workspace, "Narrow", 2, 1);
		DataPatternDefinition final =
			PatternDocumentEditor.CreateDataPattern(workspace, "Final", 4, 4);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		sequence.Entries.Add(new SequenceEntry(final.Id));
		return (PatternEditorContext.ForSequence(
			workspace.Document, sequence, 0), first, narrow, final);
	}
}
