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

    [AvaloniaTest]
    public void FlattenedCutKeepsLabelAndTemporarilyBorrowsSourceSpaceForLifecycleHint()
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
        var notes = Fields<TextBlock>(editor, "_noteTexts");
        var octaves = Fields<TextBlock>(editor, "_octaveTexts");
        var suffixes = Fields<TextBlock>(editor, "_noteSuffixTexts");
        var noteFields = Fields<Border>(editor, "_noteFields");
        var sourceFields = Fields<Border>(editor, "_sourceFields");

        TextBlock cut = notes[(24, 0)];
        TextBlock hint = suffixes[(24, 0)];
        Border cutSource = sourceFields[(24, 0)];
        Border ordinarySource = sourceFields[(0, 0)];
        Assert.Multiple(() =>
        {
            Assert.That(cut.Text, Is.EqualTo("CUT"),
                "status glyphs must not clip the fixed Note label");
            Assert.That(octaves[(24, 0)].Text, Is.Empty);
            Assert.That(hint.Text, Does.Contain("↳"));
            Assert.That(hint.IsVisible, Is.True);
            Assert.That(hint.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(((Grid)noteFields[(24, 0)].Parent!).ClipToBounds, Is.True);
            Assert.That(Grid.GetColumn(hint), Is.EqualTo(2));
            Assert.That(cutSource.Width, Is.EqualTo(124));
            Assert.That(Grid.GetColumn(cutSource), Is.EqualTo(2));
            Assert.That(Grid.GetColumnSpan(cutSource), Is.EqualTo(1));

            Assert.That(suffixes[(0, 0)].IsVisible, Is.False);
            Assert.That(ordinarySource.Width, Is.EqualTo(152));
            Assert.That(Grid.GetColumn(ordinarySource), Is.EqualTo(1));
            Assert.That(Grid.GetColumnSpan(ordinarySource), Is.EqualTo(2));
        });

        // The status region is bounded and leaves the neighbouring Source
        // field untouched, including when another condition adds a hint.
        Grid noteParts = (Grid)noteFields[(24, 0)].Parent!;
        noteParts.Measure(new Size(94, 24));
        noteParts.Arrange(new Rect(0, 0, 94, 24));
        Assert.That(hint.Bounds.Right, Is.LessThanOrEqualTo(
            noteParts.Bounds.Width + 0.01));

        typeof(PatternEditorControl).GetMethod(
            "SetNoteParts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [
                cut, octaves[(24, 0)], hint, cutSource,
                "CUT", "⚠ ⏱ "
            ]);
        Assert.Multiple(() =>
        {
            Assert.That(cut.Text, Is.EqualTo("CUT"));
            Assert.That(hint.Text, Is.EqualTo("⚠⏱"));
            Assert.That(ToolTip.GetTip(hint)?.ToString(), Does.Contain("CUT"));
            Assert.That(cutSource.Width, Is.EqualTo(124));
        });
    }

    [AvaloniaTest]
    public void MixdownBesidePitchCedesSharedAreaAndOtherRowsExpandSourceName()
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
        Border mixedSourceField = sourceFields[(0, 0)];
        Border ordinarySourceField = sourceFields[(1, 0)];
        Assert.Multiple(() =>
        {
            Assert.That(notes[(0, 0)].Text, Is.EqualTo("C-"));
            Assert.That(octaves[(0, 0)].Text, Is.EqualTo("4"));
            Assert.That(mix.Text, Is.EqualTo(" [mix]"),
                "the Mixdown marker belongs immediately after the pitch");
            Assert.That(mix.IsVisible, Is.True);
            Assert.That(mix.FontSize, Is.EqualTo(10));
            Assert.That(source.Text, Is.EqualTo(leaf.Name));
            Assert.That(source.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(badge.Child, Is.SameAs(source),
                "Mixdown must not be embedded in the coloured Source badge");
            Assert.That(badge.MaxWidth, Is.EqualTo(146));
            Assert.That(badge.Background, Is.TypeOf<SolidColorBrush>());
            Assert.That(source.Foreground, Is.SameAs(Brushes.Black));
            Assert.That(mixedSourceField.Width, Is.EqualTo(124));
            Assert.That(Grid.GetColumn(mixedSourceField), Is.EqualTo(2));
            Assert.That(Grid.GetColumnSpan(mixedSourceField), Is.EqualTo(1));
            Assert.That(suffixes[(1, 0)].IsVisible, Is.False);
            Assert.That(ordinarySourceField.Width, Is.EqualTo(152));
            Assert.That(Grid.GetColumn(ordinarySourceField), Is.EqualTo(1));
            Assert.That(Grid.GetColumnSpan(ordinarySourceField), Is.EqualTo(2));
            Assert.That(cells[(0, 0)].Width, Is.EqualTo(308));
            Assert.That(cells[(1, 0)].Width, Is.EqualTo(308));
            Assert.That(ToolTip.GetTip(mixedSourceField)?.ToString(),
                Does.StartWith(leaf.Name + " <"),
                "the Source tooltip retains the complete name and ID");
        });

        // Source text must be constrained to the available badge area.
        badge.Measure(new Size(124, 24));
        badge.Arrange(new Rect(0, 0, 124, 24));
        Assert.That(source.Bounds.Right,
            Is.LessThanOrEqualTo(badge.Bounds.Width + 0.01));

        // A note edit without Mixdown returns the space to Source without
        // rebuilding the cell, changing its width, or replacing its badge.
        middle.Grid[0, 0]!.Note = new StartPatternNote(leaf.Id);
        Refresh(editor, middle, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(suffixes[(0, 0)], Is.SameAs(mix));
            Assert.That(mix.Text, Is.Empty);
            Assert.That(mix.IsVisible, Is.False);
            Assert.That(badges[(0, 0)], Is.SameAs(badge));
            Assert.That(sourceLabels[(0, 0)], Is.SameAs(source));
            Assert.That(mixedSourceField.Width, Is.EqualTo(152));
            Assert.That(Grid.GetColumn(mixedSourceField), Is.EqualTo(1));
            Assert.That(Grid.GetColumnSpan(mixedSourceField), Is.EqualTo(2));
            Assert.That(sourceLabels[(0, 0)].Text, Is.EqualTo(leaf.Name));
        });

        // The shared area has different Note-versus-Source click owners
        // depending on whether an indicator is actually displayed.
        Assert.Multiple(() =>
        {
            Assert.That(PatternCellFieldGeometry.HitTest(80,
                308, 124, 34, 54, 0, true,
                octaveLeft: 44, octaveWidth: 22),
                Is.EqualTo(PatternCellField.Note));
            Assert.That(PatternCellFieldGeometry.HitTest(80,
                308, 152, 34, 54, 0, true,
                octaveLeft: 44, octaveWidth: 22),
                Is.EqualTo(PatternCellField.Source));
            Assert.That(PatternCellFieldGeometry.HitTest(50,
                308, 152, 34, 54, 0, true,
                octaveLeft: 44, octaveWidth: 22),
                Is.EqualTo(PatternCellField.Octave));
        });
    }
}
