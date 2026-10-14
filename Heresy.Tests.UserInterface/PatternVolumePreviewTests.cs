using System;
using System.Collections.Generic;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternVolumePreviewTests
{
    [TestCase("48", 5, "58")]
    [TestCase("03", 6, "63")]
    [TestCase("00", 0, "00")]
    [TestCase("..", 4, "40")]
    public void PendingTextUsesStoredUnitsOrZeroWithoutChangingValue(
        string committed, int tens, string expected)
    {
        Assert.That(PatternVolumePreviewRenderer.PendingText(tens, committed),
            Is.EqualTo(expected));
        Assert.That(() => PatternVolumePreviewRenderer.PendingText(7, committed),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [AvaloniaTest]
    public void PendingRendererUsesFocusBrushAndNormalThemeForegroundForSecondDigit()
    {
        foreach (IBrush normal in new IBrush[] { Brushes.White, Brushes.Black })
        {
            TextBlock label = new() { Text = "48", Foreground = normal };
            IBrush focused = Brushes.DeepSkyBlue;
            PatternVolumePreviewRenderer.ShowPending(label, 5, "48", focused);
            Assert.That(label.Inlines, Has.Count.EqualTo(2));
            Run first = (Run)label.Inlines![0];
            Run second = (Run)label.Inlines[1];
            Assert.Multiple(() =>
            {
                Assert.That(first.Text, Is.EqualTo("5"));
                Assert.That(first.Foreground, Is.SameAs(focused));
                Assert.That(second.Text, Is.EqualTo("8"));
                Assert.That(second.Foreground, Is.SameAs(normal),
                    "unstyled units digit inherits the active theme foreground");
            });
            PatternVolumePreviewRenderer.Restore(label, "48");
            Assert.That(label.Text, Is.EqualTo("48"));
            Assert.That(label.Inlines, Is.Empty);
        }
    }

    [AvaloniaTest]
    public void TwoStepTypingOnlyPreviewsFirstDigitAndCommitsAndAdvancesAfterSecond()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 3, 1);
        PatternDocumentEditor.SetVolume(workspace, pattern, 0, 0, 48.0 / 64.0);
        PatternEditorControl editor = Editor(workspace, pattern);
        SetCursor(editor, 0, 0, PatternCellField.Volume);
        var volumes = Fields<TextBlock>(editor, "_volumeTexts");
        var fields = Fields<Border>(editor, "_volumeFields");
        TextBlock label = volumes[(0, 0)];
        uint revision = workspace.Document.AudioRevision;
        uint docRevision = workspace.Document.DocumentRevision;

        Type(editor, 0, 0, "5");
        Run first = (Run)label.Inlines![0];
        Run second = (Run)label.Inlines[1];
        Assert.That(first.Text, Is.EqualTo("5"));
        Assert.That(second.Text, Is.EqualTo("8"));
        Assert.That(first.Foreground, Is.SameAs(fields[(0, 0)].BorderBrush));
        Assert.That(fields[(0, 0)].BorderBrush, Is.SameAs(Brushes.DeepSkyBlue));
        Assert.That(pattern.Grid[0, 0]!.Volume, Is.EqualTo(48.0 / 64.0),
            "a tentative tens digit never writes to the document");
        Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(docRevision));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));

        Type(editor, 0, 0, "2");
        Assert.That(pattern.Grid[0, 0]!.Volume, Is.EqualTo(52.0 / 64.0));
        Assert.That(label.Inlines, Is.Empty);
        Assert.That(label.Text, Is.EqualTo("52"));
        Assert.That(Cursor(editor).Row, Is.EqualTo(1));
        Assert.That(Cursor(editor).Field, Is.EqualTo(PatternCellField.Volume));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
    }

    [AvaloniaTest]
    public void EmptyAndRejectedInputRestoreOriginalTextWithoutWritingSongData()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 3, 1);
        PatternEditorControl editor = Editor(workspace, pattern);
        SetCursor(editor, 0, 0, PatternCellField.Volume);
        TextBlock label = Fields<TextBlock>(editor, "_volumeTexts")[(0, 0)];
        uint rev = workspace.Document.AudioRevision;
        Type(editor, 0, 0, "6");
        Assert.That(((Run)label.Inlines![0]).Text, Is.EqualTo("6"));
        Assert.That(((Run)label.Inlines[1]).Text, Is.EqualTo("0"));
        Type(editor, 0, 0, "5");
        Assert.That(label.Text, Is.EqualTo(".."));
        Assert.That(label.Inlines, Is.Empty);
        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(Cursor(editor).Row, Is.Zero);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(rev));

        Type(editor, 0, 0, "7");
        Assert.That(label.Text, Is.EqualTo(".."));
        Assert.That(label.Inlines, Is.Empty);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(rev));
    }

    [AvaloniaTest]
    public void NavigationAndEditModeChangesCancelPendingPreview()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 4, 2);
        PatternDocumentEditor.SetVolume(workspace, pattern, 0, 0, 0.5);
        PatternEditorControl editor = Editor(workspace, pattern);
        TextBlock label = Fields<TextBlock>(editor, "_volumeTexts")[(0, 0)];
        uint rev = workspace.Document.AudioRevision;

        SetCursor(editor, 0, 0, PatternCellField.Volume);
        Type(editor, 0, 0, "2");
        Assert.That(label.Inlines, Has.Count.EqualTo(2));
        SetCursor(editor, 1, 0, PatternCellField.Volume);
        Assert.That(label.Text, Is.EqualTo("32"));
        Assert.That(label.Inlines, Is.Empty);

        // The next digit on the new row starts a new pair, not the
        // second digit of the old cell's abandoned pair.
        Type(editor, 1, 0, "3");
        var next = Fields<TextBlock>(editor, "_volumeTexts")[(1, 0)];
        Assert.That(((Run)next.Inlines![0]).Text, Is.EqualTo("3"));
        Assert.That(pattern.Grid[1, 0], Is.Null);

        SetCursor(editor, 1, 1, PatternCellField.Note);
        Assert.That(next.Text, Is.EqualTo(".."));
        Assert.That(next.Inlines, Is.Empty);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(rev));
    }

    [AvaloniaTest]
    public void RefreshGridRestoresCommittedContentAndCancelsFirstDigit()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 3, 1);
        PatternEditorControl editor = Editor(workspace, pattern);
        SetCursor(editor, 0, 0, PatternCellField.Volume);
        Type(editor, 0, 0, "4");
        Assert.That(Fields<TextBlock>(editor, "_volumeTexts")[(0, 0)].Inlines,
            Has.Count.EqualTo(2));
        Invoke(editor, "RefreshGrid");
        TextBlock restored = Fields<TextBlock>(editor, "_volumeTexts")[(0, 0)];
        Assert.That(restored.Text, Is.EqualTo(".."));
        Assert.That(restored.Inlines, Is.Empty);
        Assert.That(pattern.Grid[0, 0], Is.Null);
    }

    private static PatternEditorControl Editor(
        DocumentWorkspace workspace, DataPatternDefinition pattern)
        => new(new Window(), workspace, pattern, () => { }, _ => { });

    private static void SetCursor(
        PatternEditorControl editor, int row, int channel, PatternCellField field)
    {
        Cursor(editor).SetPosition(row, channel, field);
        Invoke(editor, "RefreshCursorVisuals");
    }

    private static PatternEffectCursor Cursor(PatternEditorControl editor)
        => (PatternEffectCursor)typeof(PatternEditorControl)
            .GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), T> Fields<T>(
        PatternEditorControl editor, string name) where T : Control
        => (Dictionary<(int Row, int Channel), T>)
            typeof(PatternEditorControl)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(editor)!;

    private static void Type(PatternEditorControl editor, int row, int channel, string text)
        => Invoke(editor, "OnCellTextInput", row, channel,
            new TextInputEventArgs { Text = text });

    private static void Invoke(PatternEditorControl editor, string name,
        params object[] args)
        => typeof(PatternEditorControl).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args);
}
