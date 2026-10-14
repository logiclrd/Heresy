using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Headless end-to-end profile of native row Insert/Delete (not a microbench
/// of the underlying array shift). Timing is diagnostic, never asserted as
/// a wall-clock threshold on variable GitHub-hosted CI machines.
/// Structural assertions enforce the optimization deterministically.
/// </summary>
[TestFixture]
public sealed class PatternRowMutationPerformanceTests
{
    [AvaloniaTest]
    public void SingleChannelShiftRepaintsOnlyItsSuffixWithoutReplacingAnyControl()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Large", rowCount: 96, channelCount: 8);
        for (int row = 0; row < pattern.RowCount; row++)
            for (int channel = 0; channel < pattern.ChannelCount; channel++)
            {
                PatternCell cell = pattern.Grid.GetOrCreateCell(row, channel);
                cell.Note = new PatternNoteOff();
                cell.Volume = 0.25;
                if (row % 3 == 0)
                    cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x11));
            }
        PatternEditorControl editor = Editor(workspace, pattern);
        var before = Cells(editor);
        Border focus = before[(80, 3)];
        Border neighbor = before[(79, 3)];
        Border sibling = before[(80, 4)];
        Grid grid = Field<Grid>(editor, "_patternGrid");
        PatternCell old80 = pattern.Grid[80, 3]!;
        PatternCell old81 = pattern.Grid[81, 3]!;
        PatternCell untouched = pattern.Grid[80, 4]!;
        uint revision = workspace.Document.AudioRevision;
        Cursor(editor).SetPosition(80, 3, PatternCellField.Volume);
        bool result = Apply(editor, PatternRowMutationKind.Insert, allChannels: false);
        Assert.That(result, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(pattern.Grid[80, 3], Is.Null);
            Assert.That(pattern.Grid[81, 3], Is.SameAs(old80));
            Assert.That(pattern.Grid[82, 3], Is.SameAs(old81));
            Assert.That(pattern.Grid[80, 4], Is.SameAs(untouched));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
            Assert.That(Cursor(editor).Row, Is.EqualTo(80));
            Assert.That(Cursor(editor).Channel, Is.EqualTo(3));
            Assert.That(Cursor(editor).Field, Is.EqualTo(PatternCellField.Volume));
            Assert.That(Field<Grid>(editor, "_patternGrid"), Is.SameAs(grid),
                "the row shift must not rebuild the Avalonia grid");
            Assert.That(Cells(editor)[(80, 3)], Is.SameAs(focus));
            Assert.That(Cells(editor)[(79, 3)], Is.SameAs(neighbor));
            Assert.That(Cells(editor)[(80, 4)], Is.SameAs(sibling));
        });
        PatternRowMutationTiming timing = Timing(editor);
        Assert.Multiple(() =>
        {
            Assert.That(timing.CellsRepainted, Is.EqualTo(16));
            Assert.That(timing.RealizedCells, Is.EqualTo(96 * 8));
            Assert.That(timing.Total, Is.GreaterThanOrEqualTo(timing.DocumentMutation));
        });

        // Delete restores the original sequence without changing cell IDs,
        // effect stack contents, allocations or unrelated channels.
        result = Apply(editor, PatternRowMutationKind.Delete, allChannels: false);
        Assert.That(result, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(pattern.Grid[80, 3], Is.SameAs(old80));
            Assert.That(pattern.Grid[81, 3], Is.SameAs(old81));
            Assert.That(pattern.Grid[80, 4], Is.SameAs(untouched));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
            Assert.That(Field<Grid>(editor, "_patternGrid"), Is.SameAs(grid));
            Assert.That(Cells(editor)[(80, 3)], Is.SameAs(focus));
        });
    }

    [AvaloniaTest]
    public void ShiftInReusedSequencePatternUpdatesEveryOccurrenceButNotSibling()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition reused = PatternDocumentEditor.CreateDataPattern(
            workspace, "Repeated", rowCount: 8, channelCount: 3);
        DataPatternDefinition sibling = PatternDocumentEditor.CreateDataPattern(
            workspace, "Unrelated", rowCount: 5, channelCount: 2);
        PatternCell moved = reused.Grid.GetOrCreateCell(5, 1);
        moved.Note = new PatternNoteCut();
        moved.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x38));
        PatternCell siblingCell = sibling.Grid.GetOrCreateCell(1, 0);
        siblingCell.Note = new PatternNoteOff();
        DataSequenceDefinition sequence = new(
            workspace.Document.AllocateObjectId(), "Order");
        sequence.Entries.Add(new SequenceEntry(reused.Id));
        sequence.Entries.Add(new SequenceEntry(sibling.Id));
        sequence.Entries.Add(new SequenceEntry(reused.Id));
        workspace.Document.Add(sequence);
        PatternEditorContext context = PatternEditorContext.ForSequence(
            workspace.Document, sequence, 0);
        PatternEditorControl editor = new(new Window(), workspace, context,
            () => { }, _ => { });
        Grid grid = Field<Grid>(editor, "_patternGrid");
        var controls = Cells(editor);
        Border first = controls[(6, 1)];
        Border second = controls[(19, 1)]; // 8 + 5 + 6
        Border other = controls[(9, 0)];
        uint audioRevision = workspace.Document.AudioRevision;
        Cursor(editor).SetPosition(4, 1, PatternCellField.EffectCommand);
        Assert.That(Apply(editor, PatternRowMutationKind.Insert, allChannels: true), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(reused.Grid[6, 1], Is.SameAs(moved));
            Assert.That(sibling.Grid[1, 0], Is.SameAs(siblingCell));
            Assert.That(Cells(editor)[(6, 1)], Is.SameAs(first));
            Assert.That(Cells(editor)[(19, 1)], Is.SameAs(second));
            Assert.That(Cells(editor)[(9, 0)], Is.SameAs(other));
            Assert.That(Field<Grid>(editor, "_patternGrid"), Is.SameAs(grid));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioRevision + 1));
            Assert.That(Timing(editor).CellsRepainted, Is.EqualTo(2 * 4 * 3));
        });
    }

    [AvaloniaTest]
    public void ReleaseScaleProfileMeasuresFullRebuildAgainstBoundedInsertAndDelete()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Profile 128x8", rowCount: 128, channelCount: 8);
        for (int row = 0; row < 128; row++)
            for (int channel = 0; channel < 8; channel++)
            {
                PatternCell cell = pattern.Grid.GetOrCreateCell(row, channel);
                cell.Note = new PatternNoteOff();
                if (row % 4 == 0)
                    cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x25));
            }
        PatternEditorControl editor = Editor(workspace, pattern);
        Cursor(editor).SetPosition(104, 2, PatternCellField.Note);
        Grid original = Field<Grid>(editor, "_patternGrid");
        List<double> oldTimes = [], newTimes = [];

        // The legacy benchmark is the exact former handler:
        // PatternDocumentEditor.Insert/DeleteRow followed by RefreshGrid.
        for (int index = 0; index < 2; index++)
        {
            long start = Stopwatch.GetTimestamp();
            if (index % 2 == 0)
                PatternDocumentEditor.InsertRow(workspace, pattern, 104, 2);
            else
                PatternDocumentEditor.DeleteRow(workspace, pattern, 104, 2);
            Invoke(editor, "RefreshGrid");
            oldTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Grid retained = Field<Grid>(editor, "_patternGrid");
        for (int index = 0; index < 3; index++)
        {
            bool result = Apply(editor,
                index % 2 == 0 ? PatternRowMutationKind.Insert
                    : PatternRowMutationKind.Delete,
                allChannels: false);
            Assert.That(result, Is.True);
            PatternRowMutationTiming t = Timing(editor);
            newTimes.Add(t.Total.TotalMilliseconds);
            Assert.Multiple(() =>
            {
                Assert.That(t.CellsRepainted, Is.EqualTo(24));
                Assert.That(t.RealizedCells, Is.EqualTo(1024));
                Assert.That(Field<Grid>(editor, "_patternGrid"), Is.SameAs(retained),
                    "the optimized path must not construct a new grid");
                Assert.That(t.CellRefresh, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
                Assert.That(t.Decorations, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
                Assert.That(t.CursorAndFocus, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            });
        }
        Assert.That(original, Is.Not.SameAs(retained),
            "the legacy full refresh benchmark must really exercise a new grid");

        double oldMean = oldTimes.Average(), newMean = newTimes.Average();
        TestContext.Out.WriteLine(
            $"Pattern row mutation 128x8, channel 3, row 104: " +
            $"legacy full rebuild {oldMean:F3} ms mean ({string.Join(", ", oldTimes.Select(x => x.ToString("F3")))}); " +
            $"localized {newMean:F3} ms mean ({string.Join(", ", newTimes.Select(x => x.ToString("F3")))}); " +
            $"observed speedup {oldMean / Math.Max(newMean, 0.000001):F2}x; " +
            "24/1024 cells repainted and 0 controls reconstructed.");
        // Timing is observational to avoid CI load-based false failures.
        Assert.That(oldMean, Is.GreaterThan(0));
        Assert.That(newMean, Is.GreaterThan(0));
    }

    private static PatternEditorControl Editor(
        DocumentWorkspace workspace, DataPatternDefinition pattern)
        => new(new Window(), workspace, pattern, () => { }, _ => { });

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private static Dictionary<(int, int), Border> Cells(PatternEditorControl editor)
        => Field<Dictionary<(int, int), Border>>(editor, "_cellBorders");

    private static PatternEffectCursor Cursor(PatternEditorControl editor)
        => Field<PatternEffectCursor>(editor, "_cursor");

    private static PatternRowMutationTiming Timing(PatternEditorControl editor)
        => Field<PatternRowMutationTiming>(editor, "_lastRowMutationTiming");

    private static bool Apply(PatternEditorControl editor,
        PatternRowMutationKind kind, bool allChannels)
        => (bool)typeof(PatternEditorControl)
            .GetMethod("ApplyRowMutation",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [kind, allChannels])!;

    private static void Invoke(object target, string name)
        => target.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
