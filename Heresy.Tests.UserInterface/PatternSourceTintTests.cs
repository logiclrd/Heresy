using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourceTintTests
{
    [Test]
    public void NoteStartsUseChannelSourceMemoryButNoOtherCellKindsAreTinted()
    {
        SongDocument song = new();
        DataPatternDefinition first = Source(song, "First");
        DataPatternDefinition second = Source(song, "Second");
        DataPatternDefinition pattern = new(song.AllocateObjectId(), "Tracker")
        {
            RowCount = 9, ChannelCount = 2,
        };
        song.Add(pattern);
        pattern.Grid.GetOrCreateCell(0, 0).SourceId = first.Id;
        pattern.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
        pattern.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();
        pattern.Grid.GetOrCreateCell(3, 0).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));
        pattern.Grid.GetOrCreateCell(4, 0).Note = new StartPatternNote(second.Id);
        pattern.Grid.GetOrCreateCell(5, 0).Note = new PatternNoteCut();
        PatternCell sourceOnly = pattern.Grid.GetOrCreateCell(6, 0);
        sourceOnly.SourceId = first.Id;
        sourceOnly.Volume = 0.5;
        pattern.Grid.GetOrCreateCell(7, 0).Note = new StartPatternNote();
        // Column Source takes precedence over an inline note source,
        // matching the existing flattened-source warning resolver.
        PatternCell overrideCell = pattern.Grid.GetOrCreateCell(8, 0);
        overrideCell.SourceId = second.Id;
        overrideCell.Note = new StartPatternNote(first.Id);
        pattern.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote();

        var tint = PatternSourceTintResolver.Analyze(
            song, PatternEditorContext.ForPattern(song, pattern));
        Assert.Multiple(() =>
        {
            Assert.That(tint.Count, Is.EqualTo(4));
            Assert.That(tint[(1, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tint[(4, 0)], Is.EqualTo(second.SourceColourArgb));
            Assert.That(tint[(7, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tint[(8, 0)], Is.EqualTo(second.SourceColourArgb));
            Assert.That(tint.ContainsKey((0, 0)), Is.False);
            Assert.That(tint.ContainsKey((2, 0)), Is.False);
            Assert.That(tint.ContainsKey((3, 0)), Is.False);
            Assert.That(tint.ContainsKey((5, 0)), Is.False);
            Assert.That(tint.ContainsKey((6, 0)), Is.False);
            Assert.That(tint.ContainsKey((0, 1)), Is.False);
        });
    }

    [Test]
    public void RepeatedSequenceOccurrencesRetainDifferentInheritedSources()
    {
        SongDocument song = new();
        DataPatternDefinition first = Source(song, "A");
        DataPatternDefinition second = Source(song, "B");
        DataPatternDefinition selectA = new(song.AllocateObjectId(), "Select A")
        { RowCount = 1, ChannelCount = 1 };
        selectA.Grid.GetOrCreateCell(0, 0).SourceId = first.Id;
        song.Add(selectA);
        DataPatternDefinition selectB = new(song.AllocateObjectId(), "Select B")
        { RowCount = 1, ChannelCount = 1 };
        selectB.Grid.GetOrCreateCell(0, 0).SourceId = second.Id;
        song.Add(selectB);
        DataPatternDefinition repeated = new(song.AllocateObjectId(), "Repeated")
        { RowCount = 2, ChannelCount = 1 };
        repeated.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
        repeated.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
        song.Add(repeated);
        DataSequenceDefinition sequence = new(song.AllocateObjectId(), "Order");
        sequence.Entries.Add(new SequenceEntry(selectA.Id));
        sequence.Entries.Add(new SequenceEntry(repeated.Id));
        sequence.Entries.Add(new SequenceEntry(selectB.Id));
        sequence.Entries.Add(new SequenceEntry(repeated.Id));
        song.Add(sequence);
        var tints = PatternSourceTintResolver.Analyze(song,
            PatternEditorContext.ForSequence(song, sequence, 0));
        Assert.Multiple(() =>
        {
            Assert.That(tints.Count, Is.EqualTo(4));
            Assert.That(tints[(1, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tints[(2, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tints[(4, 0)], Is.EqualTo(second.SourceColourArgb));
            Assert.That(tints[(5, 0)], Is.EqualTo(second.SourceColourArgb));
            Assert.That(repeated.Grid[0, 0]!.SourceId.IsNone, Is.True,
                "analysis never stamps inherited source IDs into the underlying Pattern");
        });
    }

    [Test]
    public void MissingSourcesScriptsAndFlowChangesCannotInventInheritedTint()
    {
        SongDocument song = new();
        DataPatternDefinition first = Source(song, "A");
        DataPatternDefinition a = new(song.AllocateObjectId(), "Order A")
        { RowCount = 2, ChannelCount = 1 };
        a.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(first.Id);
        a.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
        a.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerOrderJumpPatternEffect(3));
        song.Add(a);
        DataPatternDefinition b = new(song.AllocateObjectId(), "Order B")
        { RowCount = 2, ChannelCount = 1 };
        b.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
        b.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(first.Id);
        song.Add(b);
        ScriptPatternDefinition script =
            new(song.AllocateObjectId(), "Dynamic");
        song.Add(script);
        DataSequenceDefinition sequence =
            new(song.AllocateObjectId(), "Order");
        sequence.Entries.Add(new SequenceEntry(a.Id));
        sequence.Entries.Add(new SequenceEntry(b.Id));
        sequence.Entries.Add(new SequenceEntry(script.Id));
        sequence.Entries.Add(new SequenceEntry(b.Id));
        song.Add(sequence);
        var tint = PatternSourceTintResolver.Analyze(song,
            PatternEditorContext.ForSequence(song, sequence, 0));
        Assert.Multiple(() =>
        {
            Assert.That(tint[(0, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tint[(1, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tint.ContainsKey((2, 0)), Is.False,
                "Bxx invalidates deterministic linear inheritance");
            Assert.That(tint[(3, 0)], Is.EqualTo(first.SourceColourArgb));
            Assert.That(tint.ContainsKey((4, 0)), Is.False,
                "the intervening scripted order may choose a different source");
            Assert.That(tint[(5, 0)], Is.EqualTo(first.SourceColourArgb));
        });
        song.Remove(first.Id);
        Assert.That(PatternSourceTintResolver.Analyze(song,
            PatternEditorContext.ForSequence(song, sequence, 0)), Is.Empty,
            "missing/tombstoned objects must not present a live source tint");
    }

    [AvaloniaTest]
    public void NoteTintComposesBelowTextAndRefreshesOnlyAffectedFields()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition sourceA =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 1, 1);
        DataPatternDefinition sourceB =
            PatternDocumentEditor.CreateDataPattern(workspace, "B", 1, 1);
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Outer", 4, 1);
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, sourceA.Id);
        PatternDocumentEditor.SetNote(workspace, pattern, 1, 0,
            new StartPatternNote());
        PatternDocumentEditor.SetNote(workspace, pattern, 3, 0,
            new StartPatternNote());
        PatternEditorControl editor = new(new Window(), workspace, pattern,
            () => { }, _ => { });
        var notes = Fields(editor, "_noteFields");
        var sources = Fields(editor, "_sourceFields");
        var cells = Fields(editor, "_cellBorders");
        AssertTint(notes[(1, 0)], sourceA.SourceColourArgb!.Value);
        AssertTint(notes[(3, 0)], sourceA.SourceColourArgb!.Value);
        Assert.That(notes[(0, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(sources[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(cells[(1, 0)].BorderBrush, Is.SameAs(Brushes.Gray));
        IBrush original = notes[(1, 0)].Background!;
        uint audio = workspace.Document.AudioRevision;

        // Source-only edit on row zero updates *later* inherited note fields,
        // but does not reconstruct controls or change an unrelated note.
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, sourceB.Id);
        Invoke(editor, "RefreshUnderlyingCell", pattern, 0, 0);
        AssertTint(notes[(1, 0)], sourceB.SourceColourArgb!.Value);
        AssertTint(notes[(3, 0)], sourceB.SourceColourArgb!.Value);
        Assert.That(notes[(1, 0)].Background, Is.Not.SameAs(original));
        IBrush retained = notes[(1, 0)].Background!;

        // A note-off edit does not displace source memory.
        PatternDocumentEditor.SetNote(workspace, pattern, 2, 0,
            new PatternNoteOff());
        Invoke(editor, "RefreshUnderlyingCell", pattern, 2, 0);
        Assert.That(notes[(1, 0)].Background, Is.SameAs(retained));
        Assert.That(notes[(2, 0)].Background, Is.SameAs(Brushes.Transparent));
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, ObjectId.None);
        Invoke(editor, "RefreshUnderlyingCell", pattern, 0, 0);
        Assert.That(notes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(notes[(3, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(sources[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(cells[(1, 0)].BorderBrush, Is.SameAs(Brushes.Gray));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3),
            "only explicit song edits advance revisions, not UI tint refresh");
    }

    private static DataPatternDefinition Source(SongDocument song, string name)
    {
        DataPatternDefinition source = new(song.AllocateObjectId(), name)
        { RowCount = 1, ChannelCount = 1 };
        song.Add(source);
        return source;
    }

    private static Dictionary<(int Row, int Channel), Border> Fields(
        PatternEditorControl editor, string name)
        => (Dictionary<(int Row, int Channel), Border>)
            typeof(PatternEditorControl).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static void Invoke(PatternEditorControl editor, string name,
        params object[] args)
        => typeof(PatternEditorControl).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args);

    private static void AssertTint(Border field, uint argb)
    {
        Assert.That(field.Background, Is.TypeOf<SolidColorBrush>());
        Color c = ((SolidColorBrush)field.Background!).Color;
        Assert.Multiple(() =>
        {
            Assert.That(c.A, Is.EqualTo((byte)(argb >> 24)));
            Assert.That(c.R, Is.EqualTo((byte)(argb >> 16)));
            Assert.That(c.G, Is.EqualTo((byte)(argb >> 8)));
            Assert.That(c.B, Is.EqualTo((byte)argb));
        });
    }
}
