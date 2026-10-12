using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEffectStatusHintTests
{
	[Test]
	public void GuidanceIsExclusiveToEffectFieldsAndDescribesEmptyAndTrackerSlots()
	{
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Note);
		PatternEffectStatusHints.ForSelection(cursor, null).Should().BeNull();

		cursor.SetPosition(0, 0, PatternCellField.Source);
		PatternEffectStatusHints.ForSelection(cursor, null).Should().BeNull();
		cursor.SetPosition(0, 0, PatternCellField.Volume);
		PatternEffectStatusHints.ForSelection(cursor, null).Should().BeNull();

		cursor.SetPosition(0, 0, PatternCellField.EffectCommand);
		PatternEffectStatusHints.ForSelection(cursor, null)
			.Should().Contain("Type a tracker command A–Z");
		PatternEffectStatusHints.ForSelection(cursor, new PatternCell())
			.Should().Contain("right-click");

		PatternCell tracker = new();
		tracker.Effects.Add(new TonePortamentoPatternEffect(0x15));
		PatternEffectStatusHints.ForSelection(cursor, tracker)
			.Should().Contain("two-digit hex parameter");
		cursor.SetPosition(0, 0, PatternCellField.EffectParameter);
		PatternEffectStatusHints.ForSelection(cursor, tracker)
			.Should().Contain("two-digit hex parameter");
	}

	[Test]
	public void CollapsedStackTakesPrecedenceOverItsNativeAndTrackerEntries()
	{
		PatternCell cell = new();
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		cell.Effects.Add(new TonePortamentoPatternEffect(0x15));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);

		PatternEffectStatusHints.ForSelection(cursor, cell)
			.Should().Be("Expand stacked effects with Enter.");
		PatternEffectStatusHints.RejectedInput(cursor, cell)
			.Should().Be("Cannot type into a collapsed effect stack. Expand stacked effects with Enter.");

		cursor.SetExpandedSelection(cell, 0, ExpandedEffectField.Native);
		PatternEffectStatusHints.ForSelection(cursor, cell)
			.Should().Be("Configure effect with Enter.");
		PatternEffectStatusHints.RejectedInput(cursor, cell)
			.Should().Be("Native effects cannot be edited as tracker text. Configure effect with Enter.");

		cursor.SetExpandedSelection(cell, 1, ExpandedEffectField.Parameter);
		PatternEffectStatusHints.ForSelection(cursor, cell)
			.Should().Contain("Enter collapses");
		PatternEffectStatusHints.RejectedInput(cursor, cell)
			.Should().Contain("Invalid tracker effect input");

		cursor.Collapse();
		PatternEffectStatusHints.ForSelection(cursor, cell)
			.Should().Be("Expand stacked effects with Enter.");
	}

	[Test]
	public void SingleNativeEffectHasActualEnterEditingHint()
	{
		PatternCell cell = new();
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		PatternEffectStatusHints.ForSelection(cursor, cell)
			.Should().Be("Configure effect with Enter.");
		PatternEffectStatusHints.RejectedInput(cursor, cell)
			.Should().Contain("Configure effect with Enter.");
	}

	[Test]
	public void InvalidOrStaleExpandedIndexDoesNotProduceMisleadingGuidance()
	{
		PatternCell cell = new();
		cell.Effects.Add(new TonePortamentoPatternEffect(0x15));
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		cursor.SetExpandedSelection(cell, 1, ExpandedEffectField.Native);
		cell.Effects.RemoveAt(1);

		PatternEffectStatusHints.ForSelection(cursor, cell).Should().BeNull();
		PatternEffectStatusHints.RejectedInput(cursor, cell)
			.Should().Contain("Invalid tracker effect input");
	}

	[AvaloniaTest]
	public void StatusAreaStacksDiagnosticAndHintWithoutOverwritingEither()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Effects", rowCount: 3, channelCount: 1);
		PatternCell mixed = pattern.Grid.GetOrCreateCell(0, 0);
		mixed.Effects.Add(new TonePortamentoPatternEffect(0x15));
		mixed.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(120));
		PatternEditorControl editor = new(new Window(), workspace, pattern,
			() => { }, _ => { });
		TextBlock diagnostic = Field<TextBlock>(editor, "_message");
		TextBlock hint = Field<TextBlock>(editor, "_effectHint");
		PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
		DockPanel root = (DockPanel)editor.Content!;
		Border status = root.Children.OfType<Border>()
			.Single(b => b.Child is StackPanel p && p.Children.Contains(diagnostic));
		StackPanel lines = (StackPanel)status.Child!;
		lines.Children.Should().Equal(diagnostic, hint);
		hint.IsVisible.Should().BeFalse();

		string existingDiagnostic = "MP3 decode diagnostic must remain visible.";
		diagnostic.Text = existingDiagnostic;
		uint revision = workspace.Document.AudioRevision;

		cursor.SetPosition(0, 0, PatternCellField.EffectCommand);
		Invoke(editor, "RefreshCursorVisuals");
		hint.IsVisible.Should().BeTrue();
		hint.Text.Should().Be("Expand stacked effects with Enter.");
		diagnostic.Text.Should().Be(existingDiagnostic);

		cursor.SetExpandedSelection(mixed, 1, ExpandedEffectField.Native);
		Invoke(editor, "RefreshCursorVisuals");
		hint.Text.Should().Be("Configure effect with Enter.");
		diagnostic.Text.Should().Be(existingDiagnostic);

		cursor.SetExpandedSelection(mixed, 0, ExpandedEffectField.Command);
		Invoke(editor, "RefreshCursorVisuals");
		hint.Text.Should().Contain("Enter collapses");
		diagnostic.Text.Should().Be(existingDiagnostic);

		cursor.SetPosition(1, 0, PatternCellField.EffectCommand);
		Invoke(editor, "RefreshCursorVisuals");
		hint.Text.Should().Be("Configure effect with Enter.");
		diagnostic.Text.Should().Be(existingDiagnostic);

		cursor.SetPosition(2, 0, PatternCellField.EffectParameter);
		Invoke(editor, "RefreshCursorVisuals");
		hint.Text.Should().Contain("right-click");

		cursor.SetPosition(2, 0, PatternCellField.Note);
		Invoke(editor, "RefreshCursorVisuals");
		hint.IsVisible.Should().BeFalse();
		hint.Text.Should().BeEmpty();
		diagnostic.Text.Should().Be(existingDiagnostic);
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[AvaloniaTest]
	public void GridRebuildAndSequenceRemappingRecomputeHintWithoutLosingDiagnostics()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Pattern", rowCount: 2, channelCount: 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetPlaybackFrequencyPatternEffect(440));
		PatternEditorControl editor = new(new Window(), workspace, pattern,
			() => { }, _ => { });
		TextBlock diagnostic = Field<TextBlock>(editor, "_message");
		TextBlock hint = Field<TextBlock>(editor, "_effectHint");
		PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
		diagnostic.Text = "A persistent editing failure.";
		cursor.SetPosition(0, 0, PatternCellField.EffectCommand);

		Invoke(editor, "RefreshGrid");
		hint.Text.Should().Be("Configure effect with Enter.");
		diagnostic.Text.Should().Be("A persistent editing failure.");

		pattern.Grid[0, 0]!.Effects.Clear();
		Invoke(editor, "RefreshGrid");
		hint.Text.Should().Contain("Type a tracker command A–Z");
		diagnostic.Text.Should().Be("A persistent editing failure.");
	}

	private static T Field<T>(PatternEditorControl editor, string name)
		=> (T)typeof(PatternEditorControl).GetField(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static void Invoke(PatternEditorControl editor, string name)
		=> typeof(PatternEditorControl).GetMethod(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, null);
}
