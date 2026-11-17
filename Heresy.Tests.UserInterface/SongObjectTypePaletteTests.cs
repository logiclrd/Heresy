using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Styling;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongObjectTypePaletteTests
{
    [Test]
    public void CanonicalPaletteCoversAllNormalAndScriptedObjectTypes()
    {
        SongDocument document = new();
        ObjectId Id() => document.AllocateObjectId();
        var objects = new (SongObject Object, Color Expected)[]
        {
            (new SampleDefinition(Id(), "Sample",
                new ExternalAssetReference(Path.GetFullPath("sample.wav"))),
                Color.FromRgb(0xFB, 0xBD, 0x73)),
            (new FmSynthDefinition(Id(), "Synth",
                new FmSynthGraph([new FmConstantNode(0, 0.0)], 0)),
                Color.FromRgb(0x73, 0xFB, 0xF3)),
            (new FilterDefinition(Id(), "Filter"),
                Color.FromRgb(0xFE, 0xE0, 0xDE)),
            (new NodeEnvelopeDefinition(Id(), "Envelope"),
                Color.FromRgb(0xD2, 0x9B, 0xF9)),
            (new ScriptEnvelopeDefinition(Id(), "Script Envelope"),
                Color.FromRgb(0xD2, 0x6C, 0xE1)),
            (new InstrumentDefinition(Id(), "Instrument"),
                Color.FromRgb(0xB2, 0xDB, 0xFA)),
            (new ScriptInstrumentDefinition(Id(), "Script Instrument"),
                Color.FromRgb(0x9A, 0xA7, 0xE5)),
            (new DataPatternDefinition(Id(), "Pattern"),
                Color.FromRgb(0xB8, 0xFA, 0xB8)),
            (new ScriptPatternDefinition(Id(), "Script Pattern"),
                Color.FromRgb(0x91, 0xF9, 0x75)),
            (new DataSequenceDefinition(Id(), "Sequence"),
                Color.FromRgb(0xFF, 0xFF, 0xC0)),
            (new ScriptSequenceDefinition(Id(), "Script Sequence"),
                Color.FromRgb(0xFF, 0xFF, 0x40)),
        };
        foreach (var (songObject, expected) in objects)
        {
            Assert.That(SongObjectTypePalette.ForObject(songObject),
                Is.EqualTo(expected), songObject.GetType().Name);
        }
        Assert.That(SongObjectTypePalette.ForObject(null), Is.Null);
    }

    [Test]
    public void FilterStageColoursInterpolateLinearlyAndClampAfterFour()
    {
        Assert.That(SongObjectTypePalette.ForFilterStages(0),
            Is.EqualTo(Color.FromRgb(0xFE, 0xE0, 0xDE)));
        Assert.That(SongObjectTypePalette.ForFilterStages(1),
            Is.EqualTo(Color.FromRgb(0xFE, 0xE0, 0xDE)));
        Assert.That(SongObjectTypePalette.ForFilterStages(2),
            Is.EqualTo(Color.FromRgb(0xFC, 0xC8, 0xC5)));
        Assert.That(SongObjectTypePalette.ForFilterStages(3),
            Is.EqualTo(Color.FromRgb(0xF9, 0xB0, 0xAC)));
        Assert.That(SongObjectTypePalette.ForFilterStages(4),
            Is.EqualTo(Color.FromRgb(0xF7, 0x98, 0x93)));
        Assert.That(SongObjectTypePalette.ForFilterStages(20),
            Is.EqualTo(Color.FromRgb(0xF7, 0x98, 0x93)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SongObjectTypePalette.ForFilterStages(-1));

        SongDocument doc = new();
        FilterDefinition filter = new(doc.AllocateObjectId(), "EQ");
        for (int count = 1; count <= 5; count++)
        {
            filter.ReplaceStages(Enumerable.Range(1, count)
                .Select(i => new FilterStageDefinition(
                    i, $"stage{i}", FilterKind.OnePole)));
            Assert.That(SongObjectTypePalette.ForObject(filter),
                Is.EqualTo(SongObjectTypePalette.ForFilterStages(count)));
        }
    }

    [AvaloniaTest]
    public void SongTreeHeaderHighlightsOnlyExistingObjectsNotFolders()
    {
        MainWindow window = new();
        try
        {
            DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
                .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            SongDocument doc = workspace.Document;
            ScriptPatternDefinition script = new(
                doc.AllocateObjectId(), "Sequence clock");
            doc.Add(script);
            FilterDefinition filter = new(
                doc.AllocateObjectId(), "Filter one");
            doc.Add(filter);
            SongTreeFolder folder = SongTreeEditor.CreateFolder(
                doc, doc.GetSectionRoot(SongTreeSection.Patterns), "Folder");

            typeof(MainWindow).GetMethod("RefreshDocumentView",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { "Palette acceptance", null });
            Dictionary<SongTreeSection, TreeView> trees =
                (Dictionary<SongTreeSection, TreeView>)typeof(MainWindow)
                    .GetField("_trees", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
            TreeViewItem[] patterns = trees[SongTreeSection.Patterns]
                .ItemsSource!.Cast<TreeViewItem>().ToArray();
            TreeViewItem scriptItem = patterns.Single(x =>
                ((SongTreeItemViewModel)x.Tag!).ObjectId == script.Id);
            Border scriptName = (Border)scriptItem.Header!;
            Assert.That(((SolidColorBrush)scriptName.Background!).Color,
                Is.EqualTo(SongObjectTypePalette.ScriptedPattern));
            Assert.That(((TextBlock)scriptName.Child!).Text,
                Is.EqualTo(script.Name));
            Assert.That(((TextBlock)scriptName.Child!).Foreground,
                Is.EqualTo(Brushes.Black));
            Assert.That(patterns.Single(x =>
                ReferenceEquals(((SongTreeItemViewModel)x.Tag!).Node, folder))
                .Header, Is.TypeOf<TextBlock>(),
                "a folder should retain the normal tree header");

            TreeViewItem filterItem = trees[SongTreeSection.Filters]
                .ItemsSource!.Cast<TreeViewItem>().Single(x =>
                    ((SongTreeItemViewModel)x.Tag!).ObjectId == filter.Id);
            Assert.That(((SolidColorBrush)((Border)filterItem.Header!)
                .Background!).Color,
                Is.EqualTo(SongObjectTypePalette.FilterOneStage));

            filter.ReplaceStages(Enumerable.Range(1, 4)
                .Select(i => new FilterStageDefinition(
                    i, $"stage{i}", FilterKind.OnePole)));
            typeof(MainWindow).GetMethod("RefreshDocumentView",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { "Filter stage changed", null });
            TreeViewItem updated = trees[SongTreeSection.Filters]
                .ItemsSource!.Cast<TreeViewItem>().Single(x =>
                    ((SongTreeItemViewModel)x.Tag!).ObjectId == filter.Id);
            Assert.That(((SolidColorBrush)((Border)updated.Header!)
                .Background!).Color,
                Is.EqualTo(SongObjectTypePalette.FilterFourStages));
        }
        finally
        {
            // Avoid asking an unshown headless window to display the
            // unsaved-document confirmation dialog during teardown.
            DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
                .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            workspace.New();
            window.Close();
        }
    }
}
