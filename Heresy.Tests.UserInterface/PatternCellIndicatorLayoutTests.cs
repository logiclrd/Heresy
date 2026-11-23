using System;
using System.Collections.Generic;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternCellIndicatorLayoutTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private const double CellWidth = 308;

    private static Dictionary<(int Row, int Channel), T> Fields<T>(
        PatternEditorControl editor, string fieldName)
        => (Dictionary<(int Row, int Channel), T>)
            typeof(PatternEditorControl).GetField(
                fieldName, PrivateInstance)!.GetValue(editor)!;

    private static void Refresh(PatternEditorControl editor,
        DataPatternDefinition pattern, int row, int channel)
        => typeof(PatternEditorControl).GetMethod(
            "RefreshUnderlyingCell", PrivateInstance)!
            .Invoke(editor, [pattern, row, channel]);

    private static void ArrangeCell(PatternEditorControl editor, int row)
    {
        Border cell = Fields<Border>(editor, "_cellBorders")[(row, 0)];
        // The headless cell is not attached to a LayoutManager. Explicitly
        // request a pass, as the live window would after a layout invalidation.
        cell.InvalidateMeasure();
        cell.Measure(new Size(CellWidth, 28));
        cell.Arrange(new Rect(0, 0, CellWidth, 28));
    }

    private static Border IndicatorArea(PatternEditorControl editor, int row)
        => (Border)Fields<TextBlock>(
            editor, "_noteSuffixTexts")[(row, 0)].Parent!;

    private static double SourceLeft(PatternEditorControl editor, int row)
    {
        Border source = Fields<Border>(editor, "_sourceFields")[(row, 0)];
        Border cell = Fields<Border>(editor, "_cellBorders")[(row, 0)];
        return source.Bounds.Left + cell.BorderThickness.Left;
    }

    private static PatternCellField FieldAt(
        PatternEditorControl editor, int row, double x)
    {
        double sourceWidthForHitTest =
            CellWidth - 34 - 54 - SourceLeft(editor, row);
        return PatternCellFieldGeometry.HitTest(
            x, CellWidth, sourceWidthForHitTest, 34, 54, 0, true,
            octaveLeft: 44, octaveWidth: 22);
    }

    [AvaloniaTest]
    public void FlattenedCutShowsFullLabelWithMeasuredAutoIndicatorAndThreeDipGap()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition leaf =
            PatternDocumentEditor.CreateDataPattern(
                workspace, "Leaf", rowCount: 4, channelCount: 1);
        PatternDocumentEditor.SetNote(workspace, leaf, 1, 0,
            new StartPatternNote());
        DataPatternDefinition middle =
            PatternDocumentEditor.CreateDataPattern(
                workspace, "Middle", rowCount: 32, channelCount: 1);
        PatternDocumentEditor.SetNote(workspace, middle, 0, 0,
            new StartPatternNote(leaf.Id));
        PatternDocumentEditor.SetNote(workspace, middle, 24, 0,
            new PatternNoteCut());

        PatternEditorControl editor = new(
            new Window(), workspace, middle, () => { }, _ => { });
        ArrangeCell(editor, 0);
        ArrangeCell(editor, 24);

        var notes = Fields<TextBlock>(editor, "_noteTexts");
        var octaves = Fields<TextBlock>(editor, "_octaveTexts");
        var suffixes = Fields<TextBlock>(editor, "_noteSuffixTexts");
        var noteFields = Fields<Border>(editor, "_noteFields");
        var sourceFields = Fields<Border>(editor, "_sourceFields");

        TextBlock cut = notes[(24, 0)];
        TextBlock hint = suffixes[(24, 0)];
        Border indicator = IndicatorArea(editor, 24);
        Border cutSource = sourceFields[(24, 0)];
        Border ordinarySource = sourceFields[(0, 0)];
        Grid content = (Grid)Fields<Border>(
            editor, "_cellBorders")[(24, 0)].Child!;

        Assert.Multiple(() =>
        {
            Assert.That(cut.Text, Is.EqualTo("CUT"),
                "the lifecycle glyph must not consume the Note name field");
            Assert.That(octaves[(24, 0)].Text, Is.Empty);
            Assert.That(hint.Text, Does.Contain("↳"));
            Assert.That(hint.IsVisible, Is.True);
            Assert.That(hint.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(indicator.Padding.Right, Is.EqualTo(3));
            Assert.That(indicator.IsVisible, Is.True);
            Assert.That(content.ColumnDefinitions[2].Width.IsAuto, Is.True);
            Assert.That(content.ColumnDefinitions[3].Width.IsStar, Is.True);
            Assert.That(content.ClipToBounds, Is.True);
            Assert.That(noteFields[(24, 0)].Parent, Is.SameAs(content));
            Assert.That(Grid.GetColumn(indicator), Is.EqualTo(2));
            Assert.That(Grid.GetColumn(cutSource), Is.EqualTo(3));
            Assert.That(indicator.Bounds.Width,
                Is.GreaterThan(3).And.LessThan(28),
                "a single lifecycle glyph should not reserve the old 28 DIPs");
            Assert.That(ordinarySource.Bounds.Width,
                Is.GreaterThan(cutSource.Bounds.Width));
            Assert.That(SourceLeft(editor, 24) - SourceLeft(editor, 0),
                Is.EqualTo(content.ColumnDefinitions[2].ActualWidth).Within(1.1),
                "one-DIP border/pixel alignment aside, the Source click boundary follows the arranged Auto column");
            Assert.That(indicator.Bounds.Right,
                Is.LessThanOrEqualTo(cutSource.Bounds.Left + 0.01),
                "the drawn indicator must not encroach on the Source badge");
            Assert.That(sourceFields[(0, 0)].Width, Is.NaN,
                "Source uses the remaining star width rather than a fixed width");
        });

        Assert.Multiple(() =>
        {
            Assert.That(FieldAt(editor, 24, SourceLeft(editor, 24) - 0.2),
                Is.EqualTo(PatternCellField.Note));
            Assert.That(FieldAt(editor, 24, SourceLeft(editor, 24) + 0.2),
                Is.EqualTo(PatternCellField.Source));
            Assert.That(FieldAt(editor, 24, 50),
                Is.EqualTo(PatternCellField.Octave));
        });

        // More than one hint increases the measured area, yet CUT stays
        // unmodified and the Source field remains the rest of the row.
        typeof(PatternEditorControl).GetMethod(
            "SetNoteParts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [cut, octaves[(24, 0)], hint,
                "CUT", "⚠ ⏱ "]);
        ArrangeCell(editor, 24);
        Assert.Multiple(() =>
        {
            Assert.That(cut.Text, Is.EqualTo("CUT"));
            Assert.That(hint.Text, Is.EqualTo("⚠⏱"));
            Assert.That(indicator.Bounds.Width, Is.GreaterThan(3));
            Assert.That(ToolTip.GetTip(hint)?.ToString(), Does.Contain("CUT"));
        });
    }

    [AvaloniaTest]
    public void MixdownBesidePitchUsesNaturalWidthAndReturnsSpaceOnRefresh()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition leaf =
            PatternDocumentEditor.CreateDataPattern(
                workspace, "New Pattern With A Long Name",
                rowCount: 3, channelCount: 1);
        DataPatternDefinition middle =
            PatternDocumentEditor.CreateDataPattern(
                workspace, "Middle", rowCount: 3, channelCount: 1);
        PatternDocumentEditor.SetSource(workspace, middle, 0, 0, leaf.Id);
        PatternDocumentEditor.SetNote(workspace, middle, 0, 0,
            new StartPatternNote(leaf.Id, mixdown: true));
        PatternDocumentEditor.SetSource(workspace, middle, 1, 0, leaf.Id);

        PatternEditorControl editor = new(
            new Window(), workspace, middle, () => { }, _ => { });
        ArrangeCell(editor, 0);
        ArrangeCell(editor, 1);
        var badges = Fields<Border>(editor, "_sourceNameBadges");
        var sourceLabels = Fields<TextBlock>(editor, "_sourceTexts");
        var suffixes = Fields<TextBlock>(editor, "_noteSuffixTexts");
        var notes = Fields<TextBlock>(editor, "_noteTexts");
        var octaves = Fields<TextBlock>(editor, "_octaveTexts");
        var sourceFields = Fields<Border>(editor, "_sourceFields");
        var cells = Fields<Border>(editor, "_cellBorders");

        Border badge = badges[(0, 0)];
        TextBlock source = sourceLabels[(0, 0)];
        TextBlock mix = suffixes[(0, 0)];
        Border indicator = IndicatorArea(editor, 0);
        Border mixedSourceField = sourceFields[(0, 0)];
        Border ordinarySourceField = sourceFields[(1, 0)];
        double mixWidth = indicator.Bounds.Width;

        Assert.Multiple(() =>
        {
            Assert.That(notes[(0, 0)].Text, Is.EqualTo("C-"));
            Assert.That(octaves[(0, 0)].Text, Is.EqualTo("4"));
            Assert.That(mix.Text, Is.EqualTo(" [mix]"));
            Assert.That(mix.IsVisible, Is.True);
            Assert.That(indicator.IsVisible, Is.True);
            Assert.That(indicator.Padding.Right, Is.EqualTo(3));
            Assert.That(indicator.Bounds.Width, Is.GreaterThan(3));
            Assert.That(source.Text, Is.EqualTo(leaf.Name));
            Assert.That(source.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(badge.Child, Is.SameAs(source),
                "[mix] belongs beside the pitch, not inside the Source badge");
            Assert.That(badge.Background, Is.TypeOf<SolidColorBrush>());
            Assert.That(source.Foreground, Is.SameAs(Brushes.Black));
            Assert.That(mixedSourceField.Bounds.Width,
                Is.LessThan(ordinarySourceField.Bounds.Width));
            Assert.That(SourceLeft(editor, 0) - SourceLeft(editor, 1),
                Is.EqualTo(((Grid)cells[(0, 0)].Child!)
                    .ColumnDefinitions[2].ActualWidth).Within(1.1),
                "Source must begin after the measured Auto-column, allowing one-DIP border alignment");
            Assert.That(indicator.Bounds.Right,
                Is.LessThanOrEqualTo(mixedSourceField.Bounds.Left + 0.01));
            Assert.That(cells[(0, 0)].Width, Is.EqualTo(CellWidth));
            Assert.That(ToolTip.GetTip(mixedSourceField)?.ToString(),
                Does.StartWith(leaf.Name + " <"));
        });

        Assert.Multiple(() =>
        {
            Assert.That(FieldAt(editor, 0, SourceLeft(editor, 0) - 0.2),
                Is.EqualTo(PatternCellField.Note));
            Assert.That(FieldAt(editor, 0, SourceLeft(editor, 0) + 0.2),
                Is.EqualTo(PatternCellField.Source));
            Assert.That(FieldAt(editor, 1, SourceLeft(editor, 1) + 0.2),
                Is.EqualTo(PatternCellField.Source));
        });

        // Toggling Mixdown off collapses both content and its 3-DIP
        // wrapper padding without rebuilding the Source badge.
        middle.Grid[0, 0]!.Note = new StartPatternNote(leaf.Id);
        Refresh(editor, middle, 0, 0);
        ArrangeCell(editor, 0);
        Assert.Multiple(() =>
        {
            Assert.That(suffixes[(0, 0)], Is.SameAs(mix));
            Assert.That(mix.Text, Is.Empty);
            Assert.That(mix.IsVisible, Is.False);
            Assert.That(indicator.IsVisible, Is.False);
            Assert.That(((Grid)cells[(0, 0)].Child!)
                .ColumnDefinitions[2].ActualWidth, Is.Zero,
                "a hidden indicator collapses its Auto column completely");
            Assert.That(badges[(0, 0)], Is.SameAs(badge));
            Assert.That(sourceLabels[(0, 0)], Is.SameAs(source));
            Assert.That(mixedSourceField.Bounds.Width, Is.GreaterThan(140),
                "Source regains its ordinary-row area when the indicator collapses");
            Assert.That(SourceLeft(editor, 0),
                Is.EqualTo(SourceLeft(editor, 1)).Within(1));
            Assert.That(sourceLabels[(0, 0)].Text, Is.EqualTo(leaf.Name));
        });
    }

    [AvaloniaTest]
    public void LongSuffixEllipsizesWithoutEliminatingSource()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(
                workspace, "Long suffix", rowCount: 1, channelCount: 1);
        PatternEditorControl editor = new(
            new Window(), workspace, pattern, () => { }, _ => { });
        TextBlock note = Fields<TextBlock>(editor, "_noteTexts")[(0, 0)];
        TextBlock octave = Fields<TextBlock>(editor, "_octaveTexts")[(0, 0)];
        TextBlock suffix = Fields<TextBlock>(editor, "_noteSuffixTexts")[(0, 0)];
        Border source = Fields<Border>(editor, "_sourceFields")[(0, 0)];

        string noteText = "C-4 s×12345678901234567890123456789 [mix]";
        typeof(PatternEditorControl).GetMethod(
            "SetNoteParts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [note, octave, suffix, noteText, "↳ ⏱ "]);
        ArrangeCell(editor, 0);
        Assert.Multiple(() =>
        {
            Assert.That(note.Text, Is.EqualTo("C-"));
            Assert.That(octave.Text, Is.EqualTo("4"));
            Assert.That(suffix.Text, Does.StartWith("↳⏱ [mix]"));
            Assert.That(IndicatorArea(editor, 0).Bounds.Width,
                Is.GreaterThan(3));
            Assert.That(source.Bounds.Width, Is.GreaterThanOrEqualTo(63),
                "excessively long suffixes are capped to preserve Source");
            Assert.That(ToolTip.GetTip(suffix)?.ToString(),
                Does.Contain("12345678901234567890123456789"));
        });
    }
}
