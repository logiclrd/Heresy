using System;
using System.Collections.Generic;
using System.Linq;
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

    [AvaloniaTest]
    public void FlattenedCutShowsFullCutLabelAndSeparateBoundedLifecycleIndicator()
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
        Assert.Multiple(() =>
        {
            Assert.That(cut.Text, Is.EqualTo("CUT"),
                "the status glyph must not consume the fixed 44-DIP Note name");
            Assert.That(octaves[(24, 0)].Text, Is.Empty);
            Assert.That(hint.Text, Does.Contain("↳"),
                "the flattened-source lifecycle marker remains visible");
            Assert.That(hint.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(((Grid)noteFields[(24, 0)].Parent!).ClipToBounds, Is.True);
            Assert.That(Grid.GetColumn(hint), Is.EqualTo(2));
            Assert.That(sourceFields[(24, 0)].Width, Is.EqualTo(96));
        });

        // The status region has a real finite width and cannot leak under
        // Source, even when additional hints are appended.
        Grid noteParts = (Grid)noteFields[(24, 0)].Parent!;
        noteParts.Measure(new Size(94, 24));
        noteParts.Arrange(new Rect(0, 0, 94, 24));
        Assert.That(hint.Bounds.Right, Is.LessThanOrEqualTo(
            noteParts.Bounds.Width + 0.01));

        // Multiple same-cell indicators still leave CUT fully readable;
        // the tooltip exposes any longer clipped status/speed information.
        typeof(PatternEditorControl).GetMethod(
            "SetNoteParts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [
                cut, octaves[(24, 0)], hint,
                Fields<TextBlock>(editor, "_mixdownBadges")[(24, 0)],
                "CUT", "⚠ ⏱ "
            ]);
        Assert.That(cut.Text, Is.EqualTo("CUT"));
        Assert.That(hint.Text, Is.EqualTo("⚠⏱"));
        Assert.That(ToolTip.GetTip(hint)?.ToString(), Does.Contain("CUT"));
    }

    [AvaloniaTest]
    public void MixdownHasVisibleMarkerWithinColouredSourceBadgeAndRefreshes()
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

        PatternEditorControl editor = new(
            new Window(), workspace, middle, () => { }, _ => { });
        var badges = Fields<Border>(editor, "_sourceNameBadges");
        var sourceLabels = Fields<TextBlock>(editor, "_sourceTexts");
        var mixMarkers = Fields<TextBlock>(editor, "_mixdownBadges");
        var suffixes = Fields<TextBlock>(editor, "_noteSuffixTexts");
        var notes = Fields<TextBlock>(editor, "_noteTexts");
        var octaves = Fields<TextBlock>(editor, "_octaveTexts");
        var sourceFields = Fields<Border>(editor, "_sourceFields");
        var cells = Fields<Border>(editor, "_cellBorders");

        Border badge = badges[(0, 0)];
        TextBlock marker = mixMarkers[(0, 0)];
        TextBlock source = sourceLabels[(0, 0)];
        Grid badgeContents = (Grid)badge.Child!;
        Assert.Multiple(() =>
        {
            Assert.That(marker.IsVisible, Is.True);
            Assert.That(marker.Text, Is.EqualTo("[mix]"));
            Assert.That(source.Text, Is.EqualTo(leaf.Name));
            Assert.That(source.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(badgeContents.Children, Is.EqualTo(new Control[] { marker, source }));
            Assert.That(badgeContents.ClipToBounds, Is.True);
            Assert.That(Grid.GetColumn(marker), Is.Zero);
            Assert.That(Grid.GetColumn(source), Is.EqualTo(1));
            Assert.That(source.Foreground, Is.SameAs(Brushes.Black));
            Assert.That(marker.Foreground, Is.SameAs(Brushes.Black),
                "the mix marker must retain contrast on coloured Source badges");
            Assert.That(badge.Background, Is.TypeOf<SolidColorBrush>());
            Assert.That(suffixes[(0, 0)].Text, Does.Not.Contain("mix"));
            Assert.That(notes[(0, 0)].Text, Is.EqualTo("C-"));
            Assert.That(octaves[(0, 0)].Text, Is.EqualTo("4"));
            Assert.That(sourceFields[(0, 0)].Width, Is.EqualTo(96));
            Assert.That(cells[(0, 0)].Width, Is.EqualTo(280));
            Assert.That(ToolTip.GetTip(sourceFields[(0, 0)])?.ToString(),
                Does.StartWith(leaf.Name + " <"),
                "the Source tooltip preserves the full name and object ID");
        });

        // The Source's measured name gets only the remaining space,
        // rather than overpainting the independently visible [mix] marker.
        badge.Measure(new Size(90, 24));
        badge.Arrange(new Rect(0, 0, 90, 24));
        Assert.Multiple(() =>
        {
            Assert.That(marker.Bounds.Width, Is.GreaterThan(0));
            Assert.That(source.Bounds.Left,
                Is.GreaterThanOrEqualTo(marker.Bounds.Right - 0.01));
            Assert.That(source.Bounds.Right,
                Is.LessThanOrEqualTo(badgeContents.Bounds.Width + 0.01));
        });

        // Source and note edits refresh the existing controls; no new
        // cell, persistent badge, or audio command is created for layout.
        middle.Grid[0, 0]!.Note = new StartPatternNote(leaf.Id);
        typeof(PatternEditorControl).GetMethod(
            "RefreshUnderlyingCell", PrivateInstance)!
            .Invoke(editor, [middle, 0, 0]);
        Assert.Multiple(() =>
        {
            Assert.That(mixMarkers[(0, 0)], Is.SameAs(marker));
            Assert.That(marker.IsVisible, Is.False);
            Assert.That(badges[(0, 0)], Is.SameAs(badge));
            Assert.That(sourceLabels[(0, 0)].Text, Is.EqualTo(leaf.Name));
        });
    }
}
