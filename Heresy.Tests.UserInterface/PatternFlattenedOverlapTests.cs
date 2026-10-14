using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
public sealed class PatternFlattenedOverlapTests
{
    private const uint Red = 0x80FF0000U;

    [Test]
    public void LaterRowAndLaterChannelStartsAreRedButEarlierOriginsNeverConflictWithThemselves()
    {
        SongDocument song = new();
        DataPatternDefinition first = Pattern(song, "First", 6, 4);
        first.Grid.GetOrCreateCell(5, 3).Note = new PatternNoteOff();
        DataPatternDefinition second = Pattern(song, "Second", 4, 2);
        second.Grid.GetOrCreateCell(3, 1).Note = new PatternNoteCut();
        DataPatternDefinition ordinary = Pattern(song, "Ordinary", 1, 1);
        DataPatternDefinition host = Pattern(song, "Host", 8, 6);

        // The second start is on the *same* row, but a later channel;
        // the third is on a later row. Both fall inside the earlier range.
        host.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(first.Id);
        host.Grid.GetOrCreateCell(0, 2).Note = new StartPatternNote(second.Id);
        host.Grid.GetOrCreateCell(2, 1).Note = new StartPatternNote(ordinary.Id);
        host.Grid.GetOrCreateCell(2, 3).Note = new StartPatternNote(ordinary.Id);
        host.Grid.GetOrCreateCell(2, 5).Note = new StartPatternNote(ordinary.Id);
        // Source-only, Off, Cut and effects never become conflicts.
        host.Grid.GetOrCreateCell(1, 1).SourceId = ordinary.Id;
        host.Grid.GetOrCreateCell(1, 2).Note = new PatternNoteOff();
        host.Grid.GetOrCreateCell(1, 3).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));
        host.Grid.GetOrCreateCell(3, 2).Note = new PatternNoteCut();

        var context = PatternEditorContext.ForPattern(song, host);
        var ranges = FlattenedSourceRangeProjection.Analyze(song, context);
        var sourceTints = PatternSourceTintResolver.Analyze(song, context);
        var displayTints = ranges.ApplyConflictTints(sourceTints);

        Assert.Multiple(() =>
        {
            Assert.That(ranges.Ranges, Has.Count.EqualTo(5),
                "even a red later flattened start retains its own range");
            Assert.That(ranges.ConflictingNoteStarts,
                Is.EquivalentTo(new[] { (0, 2), (2, 1), (2, 3) }));
            Assert.That(displayTints[(0, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(displayTints[(0, 2)], Is.EqualTo(Red));
            Assert.That(displayTints[(2, 1)], Is.EqualTo(Red));
            Assert.That(displayTints[(2, 3)], Is.EqualTo(Red));
            Assert.That(displayTints[(2, 5)], Is.EqualTo(ordinary.SourceColourArgb));
            Assert.That(sourceTints[(0, 2)], Is.EqualTo(second.SourceColourArgb),
                "diagnostic display tint must not alter effective source attribution");
            Assert.That(displayTints.ContainsKey((1, 1)), Is.False);
            Assert.That(displayTints.ContainsKey((1, 2)), Is.False);
            Assert.That(displayTints.ContainsKey((1, 3)), Is.False);
            Assert.That(displayTints.ContainsKey((3, 2)), Is.False);
        });
        Assert.That(ranges.Cells[(2, 3)].Select(piece => piece.SourceId),
            Is.EquivalentTo(new[] { first.Id, second.Id, ordinary.Id }),
            "both earlier range fills and borders survive alongside the later origin");
        Assert.That(ranges.Cells[(0, 2)].Any(piece =>
            piece.SourceId == first.Id && !piece.IsOrigin), Is.True);
        Assert.That(ranges.Cells[(0, 2)].Any(piece =>
            piece.SourceId == second.Id && piece.IsOrigin), Is.True);
        Assert.That(ranges.Ranges[0].OutlineArgb & 0xFF000000U,
            Is.EqualTo(0x66000000U));
    }

    [Test]
    public void UnknownAndPrivateMixdownLaterStartsAreWarnedButDoNotCreateRanges()
    {
        SongDocument song = new();
        DataPatternDefinition source = Pattern(song, "Source", 5, 3);
        source.Grid.GetOrCreateCell(4, 2).Note = new PatternNoteOff();
        DataPatternDefinition other = Pattern(song, "Other", 6, 4);
        other.Grid.GetOrCreateCell(5, 3).Note = new StartPatternNote();
        DataPatternDefinition host = Pattern(song, "Host", 7, 5);
        host.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(source.Id);
        host.Grid.GetOrCreateCell(1, 1).Note =
            new StartPatternNote(other.Id, mixdown: true);
        // No known Source was selected on channel 2; the authored start is
        // still visible as a conflict, not as a fabricated source colour.
        host.Grid.GetOrCreateCell(2, 2).Note = new StartPatternNote();
        host.Grid.GetOrCreateCell(3, 1).Note =
            new StartPatternNote((ObjectId)999U);
        host.Grid.GetOrCreateCell(6, 1).Note = new StartPatternNote(other.Id);

        var context = PatternEditorContext.ForPattern(song, host);
        var projection = FlattenedSourceRangeProjection.Analyze(song, context);
        var trueSources = PatternSourceTintResolver.Analyze(song, context);
        var displayed = projection.ApplyConflictTints(trueSources);
        Assert.Multiple(() =>
        {
            Assert.That(projection.Ranges, Has.Count.EqualTo(2),
                "only the first finite range and the outside last start flatten");
            Assert.That(projection.ConflictingNoteStarts,
                Is.EquivalentTo(new[] { (1, 1), (2, 2), (3, 1) }));
            Assert.That(displayed[(1, 1)], Is.EqualTo(Red));
            Assert.That(displayed[(2, 2)], Is.EqualTo(Red));
            Assert.That(displayed[(3, 1)], Is.EqualTo(Red));
            Assert.That(trueSources.ContainsKey((2, 2)), Is.False);
            Assert.That(trueSources.ContainsKey((3, 1)), Is.False);
            Assert.That(projection.Ranges.Any(r => r.FirstRow == 1), Is.False);
            Assert.That(projection.Ranges.Any(r => r.FirstRow == 2), Is.False);
            Assert.That(projection.Ranges.Any(r => r.FirstRow == 3), Is.False);
            Assert.That(displayed[(6, 1)], Is.EqualTo(other.SourceColourArgb));
        });
    }

    [Test]
    public void SourceMemoryChangesAndRemovalRestoreOriginalColourWithoutChangingSong()
    {
        SongDocument song = new();
        DataPatternDefinition large = Pattern(song, "Large", 5, 3);
        large.Grid.GetOrCreateCell(4, 2).Note = new PatternNoteOff();
        DataPatternDefinition small = Pattern(song, "Small", 1, 1);
        DataPatternDefinition later = Pattern(song, "Later", 1, 1);
        DataPatternDefinition host = Pattern(song, "Host", 7, 4);
        host.Grid.GetOrCreateCell(0, 0).SourceId = large.Id;
        host.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
        host.Grid.GetOrCreateCell(2, 1).Note = new StartPatternNote(later.Id);
        var context = PatternEditorContext.ForPattern(song, host);
        uint beforeDoc = song.DocumentRevision;
        uint beforeAudio = song.AudioRevision;

        var red = FlattenedSourceRangeProjection.Analyze(song, context);
        Assert.That(red.ConflictingNoteStarts, Does.Contain((2, 1)));
        Assert.That(red.ApplyConflictTints(PatternSourceTintResolver.Analyze(song, context))[(2, 1)],
            Is.EqualTo(Red));
        // Changing the upstream remembered Source shrinks the first range.
        host.Grid[0, 0]!.SourceId = small.Id;
        var cleared = FlattenedSourceRangeProjection.Analyze(song, context);
        var baseTints = PatternSourceTintResolver.Analyze(song, context);
        Assert.That(cleared.ConflictingNoteStarts, Is.Empty);
        Assert.That(cleared.ApplyConflictTints(baseTints)[(2, 1)],
            Is.EqualTo(later.SourceColourArgb));
        Assert.That(host.Grid[2, 1]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.Multiple(() =>
        {
            Assert.That(song.DocumentRevision, Is.EqualTo(beforeDoc));
            Assert.That(song.AudioRevision, Is.EqualTo(beforeAudio));
        });
        Assert.That(() => cleared.ApplyConflictTints(null!),
            Throws.ArgumentNullException);
    }

    [Test]
    public void SequenceOccurrencesApplyRedOnlyWhereTheInheritedSourceIsWideEnough()
    {
        SongDocument song = new();
        DataPatternDefinition wide = Pattern(song, "Wide", 4, 2);
        wide.Grid.GetOrCreateCell(3, 1).Note = new PatternNoteOff();
        DataPatternDefinition narrow = Pattern(song, "Narrow", 1, 1);
        DataPatternDefinition later = Pattern(song, "Later", 1, 1);
        DataPatternDefinition selectWide = Pattern(song, "Select wide", 1, 1);
        selectWide.Grid.GetOrCreateCell(0, 0).SourceId = wide.Id;
        DataPatternDefinition selectNarrow = Pattern(song, "Select narrow", 1, 1);
        selectNarrow.Grid.GetOrCreateCell(0, 0).SourceId = narrow.Id;
        DataPatternDefinition reused = Pattern(song, "Reused", 3, 2);
        reused.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
        reused.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote(later.Id);
        DataSequenceDefinition order = new(song.AllocateObjectId(), "Sequence");
        order.Entries.Add(new SequenceEntry(selectWide.Id));
        order.Entries.Add(new SequenceEntry(reused.Id));
        order.Entries.Add(new SequenceEntry(selectNarrow.Id));
        order.Entries.Add(new SequenceEntry(reused.Id));
        song.Add(order);

        var projection = FlattenedSourceRangeProjection.Analyze(
            song, PatternEditorContext.ForSequence(song, order, 0));
        var displayed = projection.ApplyConflictTints(
            PatternSourceTintResolver.Analyze(song,
                PatternEditorContext.ForSequence(song, order, 0)));
        Assert.Multiple(() =>
        {
            Assert.That(projection.ConflictingNoteStarts,
                Is.EquivalentTo(new[] { (2, 1) }));
            Assert.That(displayed[(2, 1)], Is.EqualTo(Red));
            Assert.That(displayed[(6, 1)], Is.EqualTo(later.SourceColourArgb));
            Assert.That(projection.Cells.ContainsKey((4, 1)), Is.False);
            Assert.That(projection.Cells.ContainsKey((6, 1)), Is.True,
                "the non-overlapping later start still has its own 1x1 range");
            Assert.That(reused.Grid[0, 0]!.SourceId.IsNone, Is.True,
                "display projection never mutates an inherited Source");
        });
    }

    [AvaloniaTest]
    public void RedCellKeepsEarlierRangeOutlineAndOpaqueEffectsAboveSourceText()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition wide =
            PatternDocumentEditor.CreateDataPattern(workspace, "Wide", 4, 3);
        wide.Grid.GetOrCreateCell(3, 2).Note = new PatternNoteOff();
        DataPatternDefinition later =
            PatternDocumentEditor.CreateDataPattern(workspace, "Later", 1, 1);
        DataPatternDefinition host =
            PatternDocumentEditor.CreateDataPattern(workspace, "Host", 6, 4);
        PatternDocumentEditor.SetNote(workspace, host, 0, 0,
            new StartPatternNote(wide.Id));
        PatternDocumentEditor.SetNote(workspace, host, 1, 1,
            new StartPatternNote(later.Id));
        host.Grid.GetOrCreateCell(1, 1).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));

        PatternEditorControl editor = new(new Window(), workspace, host,
            () => { }, _ => { });
        var backgrounds = Layers<Grid>(editor, "_cellTintLayers");
        var fragments = Layers<Grid>(editor, "_rangeOverlayLayers");
        var borders = Layers<Border>(editor, "_cellBorders");
        var sourceFields = Layers<Border>(editor, "_sourceFields");
        var effectStrips = Layers<PatternEffectStripControl>(editor, "_effectStrips");
        Grid conflictLayer = backgrounds[(1, 1)];
        AssertBrush(conflictLayer.Background, Red);
        AssertBrush(backgrounds[(0, 0)].Background, wide.SourceColourArgb!.Value);
        Assert.That(fragments[(1, 1)].Children, Has.Count.EqualTo(2),
            "later range origin and preceding shaded range are both visible");
        Assert.That(fragments[(1, 1)].IsHitTestVisible, Is.False);
        Assert.That(borders[(1, 1)].Focusable, Is.True);
        Assert.That(conflictLayer.Children.First(), Is.SameAs(fragments[(1, 1)]));
        Assert.That(conflictLayer.Children.Last(), Is.SameAs(effectStrips[(1, 1)]));
        var previousOutline = (Border)fragments[(3, 2)].Children.Single();
        AssertBrush(previousOutline.BorderBrush, 0x66_000000U
            | (wide.SourceColourArgb!.Value & 0x00FFFFFFU));
        Assert.That(previousOutline.BorderThickness.Right, Is.EqualTo(2));
        Assert.That(previousOutline.BorderThickness.Bottom, Is.EqualTo(2));
        var sticker = ((Canvas)effectStrips[(1, 1)].Content!)
            .Children.OfType<Border>().Single();
        IBrush stickerColour = sticker.Background!;
        Assert.That(((SolidColorBrush)stickerColour).Color.A, Is.EqualTo(255));
        var tooltipBefore = ToolTip.GetTip(sourceFields[(1, 1)]);
        uint beforeAudio = workspace.Document.AudioRevision;

        // Removing the earlier range removes ONLY the warning's red tint and
        // previous shadow; the later note and independent sticker remain.
        PatternDocumentEditor.SetNote(workspace, host, 0, 0, null);
        Invoke(editor, "RefreshUnderlyingCell", host, 0, 0);
        AssertBrush(conflictLayer.Background, later.SourceColourArgb!.Value);
        Assert.That(fragments[(1, 1)].Children, Has.Count.EqualTo(1));
        Assert.That(ToolTip.GetTip(sourceFields[(1, 1)]), Is.EqualTo(tooltipBefore));
        Assert.That(sticker.Background, Is.SameAs(stickerColour));
        Assert.That(borders[(1, 1)].Focusable, Is.True);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeAudio + 1));

        PatternDocumentEditor.SetNote(workspace, host, 0, 0,
            new StartPatternNote(wide.Id));
        Invoke(editor, "RefreshUnderlyingCell", host, 0, 0);
        AssertBrush(conflictLayer.Background, Red);
        Assert.That(fragments[(1, 1)].Children, Has.Count.EqualTo(2));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeAudio + 2));
    }

    private static DataPatternDefinition Pattern(
        SongDocument song, string name, int rows, int channels)
    {
        DataPatternDefinition pattern = new(song.AllocateObjectId(), name)
        { RowCount = rows, ChannelCount = channels };
        song.Add(pattern);
        return pattern;
    }

    private static Dictionary<(int Row, int Channel), T> Layers<T>(
        PatternEditorControl editor, string field) where T : Control
        => (Dictionary<(int Row, int Channel), T>)
            typeof(PatternEditorControl).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static void AssertBrush(IBrush? brush, uint argb)
    {
        Assert.That(brush, Is.TypeOf<SolidColorBrush>());
        Color color = ((SolidColorBrush)brush!).Color;
        Assert.Multiple(() =>
        {
            Assert.That(color.A, Is.EqualTo((byte)(argb >> 24)));
            Assert.That(color.R, Is.EqualTo((byte)(argb >> 16)));
            Assert.That(color.G, Is.EqualTo((byte)(argb >> 8)));
            Assert.That(color.B, Is.EqualTo((byte)argb));
        });
    }

    private static void Invoke(PatternEditorControl editor, string name,
        params object[] args)
        => typeof(PatternEditorControl).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args);
}
