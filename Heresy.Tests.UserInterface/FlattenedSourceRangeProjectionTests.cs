using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FlattenedSourceRangeProjectionTests
{
    [Test]
    public void PatternBoundsDrawAcrossBothAxesIncludingNoteOffAndIgnoreTrailingEffects()
    {
        SongDocument song = new();
        DataPatternDefinition source = Pattern(song, "Child", 12, 5);
        source.Grid.GetOrCreateCell(7, 2).Note = new PatternNoteOff();
        source.Grid.GetOrCreateCell(11, 4).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));
        DataPatternDefinition host = Pattern(song, "Parent", 12, 5);
        host.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote(source.Id);

        var projection = FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host));
        FlattenedSourceRange rect = projection.Ranges.Single();
        Assert.Multiple(() =>
        {
            Assert.That((rect.FirstRow, rect.FirstChannel), Is.EqualTo((1, 1)));
            Assert.That((rect.LastRow, rect.LastChannel), Is.EqualTo((8, 3)));
            Assert.That(rect.FillArgb, Is.EqualTo(source.SourceColourArgb));
            Assert.That(rect.OutlineArgb, Is.EqualTo(
                (source.SourceColourArgb!.Value & 0x00FFFFFFU) | 0x66000000U));
            Assert.That(projection.Cells.Count, Is.EqualTo(8 * 3));
            Assert.That(projection.Cells.ContainsKey((8, 4)), Is.False);
            Assert.That(projection.Cells.ContainsKey((9, 1)), Is.False);
        });
        var origin = projection.Cells[(1, 1)].Single();
        Assert.That(origin.IsOrigin, Is.True);
        Assert.That(origin.LeftEdge && origin.TopEdge, Is.True);
        Assert.That(origin.RightEdge || origin.BottomEdge, Is.False);
        var interior = projection.Cells[(2, 2)].Single();
        Assert.That(interior.IsOrigin || interior.LeftEdge || interior.TopEdge
            || interior.RightEdge || interior.BottomEdge, Is.False);
        var corner = projection.Cells[(8, 3)].Single();
        Assert.That(corner.RightEdge && corner.BottomEdge, Is.True);
        Assert.That(corner.LeftEdge || corner.TopEdge, Is.False);
    }

    [Test]
    public void ClippingAtHostBottomAndRightAndOneByOneOutlinesAreExact()
    {
        SongDocument song = new();
        DataPatternDefinition large = Pattern(song, "Large", 12, 7);
        large.Grid.GetOrCreateCell(10, 6).Note = new StartPatternNote();
        DataPatternDefinition tiny = Pattern(song, "Empty", 8, 4);
        DataPatternDefinition host = Pattern(song, "Host", 4, 3);
        host.Grid.GetOrCreateCell(2, 2).Note = new StartPatternNote(large.Id);
        host.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(tiny.Id);

        var projected = FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host));
        Assert.That(projected.Ranges, Has.Count.EqualTo(2));
        FlattenedSourceRange empty = projected.Ranges.Single(r => r.SourceId == tiny.Id);
        Assert.That((empty.FirstRow, empty.FirstChannel, empty.LastRow, empty.LastChannel),
            Is.EqualTo((0, 0, 0, 0)));
        var single = projected.Cells[(0, 0)].Single();
        Assert.That(single.IsOrigin && single.LeftEdge && single.RightEdge
            && single.TopEdge && single.BottomEdge, Is.True);

        FlattenedSourceRange clipped = projected.Ranges.Single(r => r.SourceId == large.Id);
        Assert.That((clipped.FirstRow, clipped.FirstChannel, clipped.LastRow, clipped.LastChannel),
            Is.EqualTo((2, 2, 3, 2)));
        Assert.That(projected.Cells[(3, 2)].Single().BottomEdge, Is.True);
        Assert.That(projected.Cells[(3, 2)].Single().RightEdge, Is.True);

        host.Grid[2, 2]!.Note = new StartPatternNote(large.Id, mixdown: true);
        var withoutMixdown = FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host));
        Assert.That(withoutMixdown.Ranges.Select(r => r.SourceId), Is.EquivalentTo(
            new[] { tiny.Id }));
    }

    [Test]
    public void CompositeSequenceAccountsForPatternDurationsAndSkippedPrefixes()
    {
        SongDocument song = new();
        DataPatternDefinition a = Pattern(song, "A", 5, 3);
        a.Grid.GetOrCreateCell(1, 2).Note = new PatternNoteOff();
        DataPatternDefinition b = Pattern(song, "B", 6, 4);
        b.Grid.GetOrCreateCell(1, 3).Note = new PatternNoteCut();
        b.Grid.GetOrCreateCell(4, 1).Note = new StartPatternNote();
        DataSequenceDefinition nested = new(song.AllocateObjectId(), "Nested");
        nested.Entries.Add(new SequenceEntry(a.Id));
        nested.Entries.Add(new SequenceEntry(b.Id, startRow: 2));
        song.Add(nested);
        DataPatternDefinition host = Pattern(song, "Host", 20, 5);
        host.Grid.GetOrCreateCell(2, 1).Note = new StartPatternNote(nested.Id);

        var range = FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host)).Ranges.Single();
        // First full Pattern plays 5 rows; second starts at its row 2, so
        // last note at row 4 maps to nested offset 5 + (4-2) = 7.
        Assert.That((range.FirstRow, range.FirstChannel, range.LastRow, range.LastChannel),
            Is.EqualTo((2, 1, 9, 3)));
        Assert.That(range.LastChannel, Is.EqualTo(3),
            "a skipped note in channel 4 cannot increase the composite width");
    }

    [Test]
    public void DynamicOrUnavailableSequencePartsAndFlowTransfersDoNotGuessBounds()
    {
        SongDocument song = new();
        DataPatternDefinition data = Pattern(song, "Data", 4, 2);
        data.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteOff();
        ScriptPatternDefinition script = new(song.AllocateObjectId(), "Script");
        song.Add(script);
        DataSequenceDefinition unknown = new(song.AllocateObjectId(), "Unknown");
        unknown.Entries.Add(new SequenceEntry(data.Id));
        unknown.Entries.Add(new SequenceEntry(script.Id));
        song.Add(unknown);
        DataPatternDefinition host = Pattern(song, "Host", 10, 3);
        host.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(unknown.Id);

        Assert.That(FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host)).Ranges, Is.Empty);
        unknown.Entries.RemoveAt(1);
        data.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new TrackerPatternBreakPatternEffect(2));
        Assert.That(FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host)).Ranges, Is.Empty);
        data.Grid[0, 0]!.Effects.Clear();
        Assert.That(FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host)).Ranges, Has.Count.EqualTo(1));

        host.Grid[0, 0]!.Note = new StartPatternNote(script.Id);
        Assert.That(FlattenedSourceRangeProjection.Analyze(song,
            PatternEditorContext.ForPattern(song, host)).Ranges, Is.Empty);
    }

    [Test]
    public void SequenceEditorClipsEachRepeatedHostOccurrenceToOwnDisplayedSegment()
    {
        SongDocument song = new();
        DataPatternDefinition source = Pattern(song, "Wide", 10, 3);
        source.Grid.GetOrCreateCell(9, 2).Note = new StartPatternNote();
        DataPatternDefinition parent = Pattern(song, "Repeat", 3, 3);
        parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(source.Id);
        DataPatternDefinition other = Pattern(song, "Other", 2, 1);
        DataSequenceDefinition order = new(song.AllocateObjectId(), "Order");
        order.Entries.Add(new SequenceEntry(parent.Id));
        order.Entries.Add(new SequenceEntry(other.Id));
        order.Entries.Add(new SequenceEntry(parent.Id, startRow: 1));
        song.Add(order);

        var context = PatternEditorContext.ForSequence(song, order, 0);
        var projection = FlattenedSourceRangeProjection.Analyze(song, context);
        Assert.That(projection.Ranges, Has.Count.EqualTo(2));
        Assert.That((projection.Ranges[0].FirstRow, projection.Ranges[0].LastRow),
            Is.EqualTo((1, 2)));
        Assert.That((projection.Ranges[1].FirstRow, projection.Ranges[1].LastRow),
            Is.EqualTo((5, 6)));
        Assert.That(projection.Cells.ContainsKey((3, 1)), Is.False,
            "the intervening Pattern must not be shaded by either occurrence");
        Assert.That(projection.Cells.ContainsKey((4, 0)), Is.False);
        Assert.That(projection.Cells[(5, 0)].Single().TopEdge, Is.True);
    }

    [Test]
    public void SourceMemoryAndCacheEditsReprojectWithoutChangingRevisions()
    {
        SongDocument song = new();
        DataPatternDefinition source = Pattern(song, "Child", 8, 3);
        DataPatternDefinition host = Pattern(song, "Host", 9, 4);
        host.Grid.GetOrCreateCell(0, 1).SourceId = source.Id;
        host.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote();
        var context = PatternEditorContext.ForPattern(song, host);
        uint docRev = song.DocumentRevision;
        uint audioRev = song.AudioRevision;

        var original = FlattenedSourceRangeProjection.Analyze(song, context);
        Assert.That(original.Ranges.Single().LastRow, Is.EqualTo(1));
        source.Grid.GetOrCreateCell(5, 2).Note = new PatternNoteCut();
        var resized = FlattenedSourceRangeProjection.Analyze(song, context);
        Assert.That((resized.Ranges.Single().LastRow, resized.Ranges.Single().LastChannel),
            Is.EqualTo((6, 3)));
        source.Grid[5, 2]!.Note = null;
        Assert.That(FlattenedSourceRangeProjection.Analyze(song, context)
            .Ranges.Single().LastRow, Is.EqualTo(1));
        host.Grid[1, 1]!.Note = new StartPatternNote(source.Id, mixdown: true);
        Assert.That(FlattenedSourceRangeProjection.Analyze(song, context).Ranges, Is.Empty);
        Assert.That(song.DocumentRevision, Is.EqualTo(docRev));
        Assert.That(song.AudioRevision, Is.EqualTo(audioRev));
    }

    [AvaloniaTest]
    public void RangePaintKeepsOpaqueEffectStickersAndFocusAboveNoninteractiveOutline()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition source =
            PatternDocumentEditor.CreateDataPattern(workspace, "Source", 4, 2);
        source.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteOff();
        DataPatternDefinition host =
            PatternDocumentEditor.CreateDataPattern(workspace, "Host", 5, 3);
        PatternDocumentEditor.SetNote(workspace, host, 1, 0,
            new StartPatternNote(source.Id));
        host.Grid.GetOrCreateCell(2, 1).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));

        PatternEditorControl editor = new(new Window(), workspace, host,
            () => { }, _ => { });
        var rangeLayers = Layers(editor);
        var cells = Borders(editor);
        var tints = TintLayers(editor);
        var effects = Effects(editor);

        Assert.That(rangeLayers[(1, 0)].Children, Has.Count.EqualTo(1));
        Border origin = (Border)rangeLayers[(1, 0)].Children.Single();
        Assert.That(origin.Background, Is.SameAs(Brushes.Transparent));
        Assert.That(origin.BorderThickness.Left, Is.EqualTo(2));
        Assert.That(origin.BorderThickness.Top, Is.EqualTo(2));
        Assert.That(origin.BorderThickness.Right, Is.Zero);
        Assert.That(origin.BorderThickness.Bottom, Is.Zero);
        Assert.That(origin.IsHitTestVisible, Is.False);
        Assert.That(rangeLayers[(1, 0)].IsHitTestVisible, Is.False);
        Assert.That(cells[(1, 0)].Child, Is.SameAs(tints[(1, 0)]));
        Assert.That(tints[(1, 0)].Children.First(), Is.SameAs(rangeLayers[(1, 0)]));
        Assert.That(tints[(1, 0)].Children.Last(), Is.SameAs(effects[(1, 0)]));
        Assert.That(cells[(1, 0)].Focusable, Is.True);

        Border interior = (Border)rangeLayers[(2, 1)].Children.Single();
        Assert.That(interior.Background, Is.TypeOf<SolidColorBrush>());
        Color fill = ((SolidColorBrush)interior.Background!).Color;
        Assert.That(fill.A, Is.EqualTo(0x33));
        Border final = (Border)rangeLayers[(3, 1)].Children.Single();
        Assert.That(final.BorderThickness.Bottom, Is.EqualTo(2));
        Assert.That(final.BorderThickness.Right, Is.EqualTo(2));
        Assert.That(((SolidColorBrush)final.BorderBrush!).Color.A, Is.EqualTo(0x66));
        Canvas effectCanvas = (Canvas)effects[(2, 1)].Content!;
        Border sticker = effectCanvas.Children.OfType<Border>().Single();
        Assert.That(((SolidColorBrush)sticker.Background!).Color.A, Is.EqualTo(255));

        uint rev = workspace.Document.AudioRevision;
        Border previously = (Border)rangeLayers[(3, 1)].Children.Single();
        source.Grid.GetOrCreateCell(3, 1).Note = new PatternNoteOff();
        Invoke(editor, "RefreshUnderlyingCell", host, 1, 0);
        Assert.That(rangeLayers[(3, 1)].Children.Single(),
            Is.Not.SameAs(previously), "stale edge fragments must be recomputed");
        Assert.That(effects[(2, 1)].Content, Is.SameAs(effectCanvas));
        Assert.That(sticker.Background, Is.TypeOf<SolidColorBrush>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(rev));
    }

    private static DataPatternDefinition Pattern(
        SongDocument song, string name, int rows, int channels)
    {
        DataPatternDefinition pattern = new(song.AllocateObjectId(), name)
        { RowCount = rows, ChannelCount = channels };
        song.Add(pattern);
        return pattern;
    }

    private static Dictionary<(int Row, int Channel), Grid> Layers(PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), Grid>)
            typeof(PatternEditorControl).GetField("_rangeOverlayLayers",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), Grid> TintLayers(PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), Grid>)
            typeof(PatternEditorControl).GetField("_cellTintLayers",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), Border> Borders(PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), Border>)
            typeof(PatternEditorControl).GetField("_cellBorders",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), PatternEffectStripControl> Effects(
        PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), PatternEffectStripControl>)
            typeof(PatternEditorControl).GetField("_effectStrips",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static void Invoke(PatternEditorControl editor, string method, params object[] args)
        => typeof(PatternEditorControl).GetMethod(method,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args);
}
