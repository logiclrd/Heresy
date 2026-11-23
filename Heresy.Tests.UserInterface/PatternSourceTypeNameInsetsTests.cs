using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.Assets;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Styling;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourceTypeNameInsetsTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, PrivateInstance)!
            .GetValue(owner)!;

    private static void RefreshCell(PatternEditorControl editor, int row, int channel)
        => typeof(PatternEditorControl).GetMethod(
            "RefreshCell", PrivateInstance)!.Invoke(editor, [row, channel]);

    private static (Border Row, Border Name, TextBlock Text) Render(
        Avalonia.Controls.Templates.IDataTemplate template,
        PatternSourceOption option)
    {
        Border row = (Border)template.Build(option)!;
        Border name = (Border)row.Child!;
        return (row, name, (TextBlock)name.Child!);
    }

    private static Color Background(Border border)
        => ((SolidColorBrush)border.Background!).Color;

    private static void AssertSourceTint(Border row, uint argb)
    {
        Color colour = Background(row);
        Assert.Multiple(() =>
        {
            Assert.That(colour.A, Is.EqualTo((byte)(argb >> 24)));
            Assert.That(colour.R, Is.EqualTo((byte)(argb >> 16)));
            Assert.That(colour.G, Is.EqualTo((byte)(argb >> 8)));
            Assert.That(colour.B, Is.EqualTo((byte)argb));
        });
    }

    [AvaloniaTest]
    public void ToolbarAndCellPopupRetainPerSourceRowTintAroundTypeNameInsets()
    {
        DocumentWorkspace workspace = new();
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        SampleDefinition sample = new(sampleId, "Kick",
            new ExternalAssetReference(Path.GetFullPath("kick.wav")));
        workspace.Document.Add(sample);
        ObjectId scriptId = workspace.Document.AllocateObjectId();
        ScriptInstrumentDefinition script = new(scriptId, "Synth Script");
        workspace.Document.Add(script);
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Tracker", 2, 1);
        PatternEditorControl editor = new(
            new Window(), workspace, pattern, () => { }, _ => { });
        ComboBox toolbar = Field<ComboBox>(editor, "_noteSource");
        PatternSourceOption[] options =
            Field<PatternSourceOption[]>(editor, "_noteSources");
        PatternSourceOption kick = options.Single(x => x.Id == sampleId);
        PatternSourceOption scripted = options.Single(x => x.Id == scriptId);
        Assert.That(toolbar.ItemTemplate, Is.Not.Null);
        ListBox list = (ListBox)typeof(PatternEditorControl)
            .GetMethod("BuildSourcePopupList", PrivateInstance)!
            .Invoke(editor, [options])!;
        Assert.That(list.ItemTemplate, Is.Not.Null);

        foreach (var template in new[] { toolbar.ItemTemplate!, list.ItemTemplate! })
        {
            var (sampleRow, sampleName, sampleText) = Render(template, kick);
            AssertSourceTint(sampleRow, sample.SourceColourArgb!.Value);
            Assert.That(Background(sampleName),
                Is.EqualTo(SongObjectTypePalette.Sample));
            Assert.That(sampleText.Text, Is.EqualTo("Kick"));
            Assert.That(sampleText.Text, Does.Not.Contain("<"),
                "the visible inset is the name; the ID stays in the tooltip");
            Assert.That(ToolTip.GetTip(sampleName), Is.EqualTo(kick.DisplayName));
            Assert.That(sampleName.CornerRadius, Is.EqualTo(new CornerRadius(2)));
            Assert.That(sampleName.Padding.Top, Is.Zero);
            Assert.That(sampleRow.Padding.Top, Is.Zero);

            var (scriptRow, scriptName, scriptText) = Render(template, scripted);
            AssertSourceTint(scriptRow, script.SourceColourArgb!.Value);
            Assert.That(Background(scriptName),
                Is.EqualTo(SongObjectTypePalette.ScriptedInstrument));
            Assert.That(scriptText.Text, Is.EqualTo("Synth Script"));
            Assert.That(Background(sampleName), Is.Not.EqualTo(Background(scriptName)),
                "different object kinds use independent inset backgrounds");
            Assert.That(sampleRow.Background, Is.Not.SameAs(sampleName.Background),
                "the row tint and the object's type background are separate");
            // No extra vertical template padding: the UI's existing source
            // ComboBox/ListBox item heights must not expand.
            sampleRow.Measure(new Size(300, 100));
            Assert.That(sampleRow.DesiredSize.Height,
                Is.LessThanOrEqualTo(22));
        }

        toolbar.SelectedItem = scripted;
        var (selectedRow, selectedName, _) =
            Render(toolbar.ItemTemplate!, (PatternSourceOption)toolbar.SelectedItem!);
        AssertSourceTint(selectedRow, script.SourceColourArgb!.Value);
        Assert.That(Background(selectedName),
            Is.EqualTo(SongObjectTypePalette.ScriptedInstrument));

        // Source choices are snapshots, but rendering must consult the live
        // document to show a referenced object's current name and tooltip.
        sample.Name = "Updated Kick";
        foreach (var template in new[] { toolbar.ItemTemplate!, list.ItemTemplate! })
        {
            var (_, renamedBadge, renamedText) = Render(template, kick);
            Assert.That(renamedText.Text, Is.EqualTo("Updated Kick"));
            Assert.That(ToolTip.GetTip(renamedBadge),
                Is.EqualTo($"Updated Kick <{sampleId.Value}>"));
            Assert.That(Background(renamedBadge),
                Is.EqualTo(SongObjectTypePalette.Sample));
        }
    }

    [AvaloniaTest]
    public void PatternSourceFieldNameInsetLeavesTwentyFourDipCellAndNoteTintUntouched()
    {
        DocumentWorkspace workspace = new();
        ObjectId sourceId = workspace.Document.AllocateObjectId();
        SampleDefinition source = new(sourceId, "Bass",
            new ExternalAssetReference(Path.GetFullPath("bass.wav")));
        workspace.Document.Add(source);
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Entry", 3, 1);
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, sourceId);
        PatternDocumentEditor.SetNote(workspace, pattern, 0, 0,
            new StartPatternNote());
        PatternDocumentEditor.SetSource(workspace, pattern, 2, 0, sourceId);
        PatternEditorControl editor = new(
            new Window(), workspace, pattern, () => { }, _ => { });
        var sourceFields = Field<Dictionary<(int Row, int Channel), Border>>(
            editor, "_sourceFields");
        var sourceTexts = Field<Dictionary<(int Row, int Channel), TextBlock>>(
            editor, "_sourceTexts");
        var badges = Field<Dictionary<(int Row, int Channel), Border>>(
            editor, "_sourceNameBadges");
        var cells = Field<Dictionary<(int Row, int Channel), Border>>(
            editor, "_cellBorders");
        var tints = Field<Dictionary<(int Row, int Channel), Grid>>(
            editor, "_cellTintLayers");
        uint originalAudioRevision = workspace.Document.AudioRevision;

        foreach (int row in new[] { 0, 2 })
        {
            Border field = sourceFields[(row, 0)];
            Border badge = badges[(row, 0)];
            Assert.That(cells[(row, 0)].Height, Is.EqualTo(28));
            Assert.That(field.Height, Is.EqualTo(24));
            Assert.That(field.Child, Is.SameAs(badge));
            Assert.That(badge.Child, Is.TypeOf<Grid>());
            Grid contents = (Grid)badge.Child!;
            Assert.That(contents.Children.Last(),
                Is.SameAs(sourceTexts[(row, 0)]));
            Assert.That(contents.ColumnDefinitions, Has.Count.EqualTo(2));
            Assert.That(contents.ClipToBounds, Is.True);
            Assert.That(badge.CornerRadius, Is.EqualTo(new CornerRadius(2)));
            Assert.That(badge.Padding.Top, Is.Zero);
            Assert.That(badge.Padding.Bottom, Is.Zero);
            Assert.That(badge.MaxWidth, Is.LessThan(96));
            Assert.That(Background(badge), Is.EqualTo(SongObjectTypePalette.Sample));
            Assert.That(sourceTexts[(row, 0)].Text, Is.EqualTo("Bass"));
            Assert.That(ToolTip.GetTip(field), Does.Contain("Bass"));
            field.Measure(new Size(96, 100));
            Assert.That(field.DesiredSize.Height, Is.EqualTo(24));
        }
        // The existing full-cell Note-start source tint is retained.
        Color tint = ((SolidColorBrush)tints[(0, 0)].Background!).Color;
        Assert.That(tint.A, Is.EqualTo((byte)(source.SourceColourArgb!.Value >> 24)));
        Assert.That(tints[(2, 0)].Background, Is.SameAs(Brushes.Transparent),
            "source-only rows were intentionally never Note-start tinted");
        Assert.That(sourceFields[(0, 0)].Background,
            Is.SameAs(Brushes.Transparent));

        // Refreshing a referenced object name updates the same TextBlock,
        // not the row's height, Source ID, or any audio state.
        source.Name = "Long Live Bass Patch";
        Border retained = badges[(0, 0)];
        RefreshCell(editor, 0, 0);
        Assert.That(badges[(0, 0)], Is.SameAs(retained));
        Assert.That(sourceTexts[(0, 0)].Text, Is.EqualTo("Long Live Bass Patch"));
        Assert.That(sourceTexts[(0, 0)].TextTrimming,
            Is.EqualTo(TextTrimming.CharacterEllipsis));
        Assert.That(cells[(0, 0)].Height, Is.EqualTo(28));
        Assert.That(workspace.Document.AudioRevision,
            Is.EqualTo(originalAudioRevision));

        // Removing the referenced object clears just its type inset;
        // missing-reference warning text remains available.
        workspace.Document.Remove(sourceId);
        RefreshCell(editor, 0, 0);
        Assert.That(badges[(0, 0)].Background,
            Is.SameAs(Brushes.Transparent));
        Assert.That(sourceTexts[(0, 0)].Text, Does.StartWith("⚠"));
    }
}
