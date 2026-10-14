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
    public void WholeCellTintComposesOverRowShadeBelowIndependentEffectTabs()
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
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerVolumeSlidePatternEffect(0x12));
        PatternEditorControl editor = new(new Window(), workspace, pattern,
            () => { }, _ => { });
        var notes = Fields(editor, "_noteFields");
        var sources = Fields(editor, "_sourceFields");
        var volumes = Fields(editor, "_volumeFields");
        var cells = Fields(editor, "_cellBorders");
        var tints = TintLayers(editor);
        var effects = EffectStrips(editor);

        // The entire inner grid has the translucent source background, not
        // the Note-only subfield. Its border parent's row shade remains a
        // distinct underlayer for beat/selection/playback-follow repaint.
        Assert.That(cells[(1, 0)].Child, Is.SameAs(tints[(1, 0)]));
        AssertTint(tints[(1, 0)], sourceA.SourceColourArgb!.Value);
        AssertTint(tints[(3, 0)], sourceA.SourceColourArgb!.Value);
        Assert.That(tints[(0, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(notes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(sources[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(volumes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(cells[(1, 0)].BorderBrush, Is.SameAs(Brushes.Gray));
        IBrush rowShade = cells[(1, 0)].Background!;
        IBrush original = tints[(1, 0)].Background!;

        // Sticker is the last painted overlay; it has its own opaque effect
        // palette background and never inherits the underlying source hue.
        Assert.That(tints[(1, 0)].Children.Last(), Is.SameAs(effects[(1, 0)]));
        Canvas effectCanvas = (Canvas)effects[(1, 0)].Content!;
        Border effectTab = effectCanvas.Children.OfType<Border>().Single();
        IBrush effectBrush = effectTab.Background!;
        Assert.That(effectBrush, Is.TypeOf<SolidColorBrush>());
        Assert.That(((SolidColorBrush)effectBrush).Color.A, Is.EqualTo(255));
        uint audio = workspace.Document.AudioRevision;

        // A Source-only edit repaints *later* inherited whole cells in-place,
        // not their child fields, border/focus/selection shading or stickers.
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, sourceB.Id);
        Invoke(editor, "RefreshUnderlyingCell", pattern, 0, 0);
        AssertTint(tints[(1, 0)], sourceB.SourceColourArgb!.Value);
        AssertTint(tints[(3, 0)], sourceB.SourceColourArgb!.Value);
        Assert.That(tints[(1, 0)].Background, Is.Not.SameAs(original));
        Assert.That(cells[(1, 0)].Background, Is.SameAs(rowShade));
        Assert.That(notes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(sources[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(volumes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(effectTab.Background, Is.SameAs(effectBrush));
        IBrush retained = tints[(1, 0)].Background!;

        // Reapplying focus, row and selection presentation cannot erase the
        // independent cell tint. Note Off never acquires a source colour.
        Invoke(editor, "RefreshCursorVisuals");
        Assert.That(tints[(1, 0)].Background, Is.SameAs(retained));
        PatternDocumentEditor.SetNote(workspace, pattern, 2, 0,
            new PatternNoteOff());
        Invoke(editor, "RefreshUnderlyingCell", pattern, 2, 0);
        Assert.That(tints[(1, 0)].Background, Is.SameAs(retained));
        Assert.That(tints[(2, 0)].Background, Is.SameAs(Brushes.Transparent));
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, ObjectId.None);
        Invoke(editor, "RefreshUnderlyingCell", pattern, 0, 0);
        Assert.That(tints[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(tints[(3, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(notes[(1, 0)].Background, Is.SameAs(Brushes.Transparent));
        Assert.That(effectTab.Background, Is.SameAs(effectBrush));
        Assert.That(cells[(1, 0)].BorderBrush, Is.SameAs(Brushes.Gray));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3),
            "only explicit song edits advance revisions, not UI tint refresh");
    }

    [AvaloniaTest]
    public void RepeatedSequenceOccurrencesUpdateOnlyAffectedWholeCellTints()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition sourceA =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 1, 1);
        DataPatternDefinition sourceB =
            PatternDocumentEditor.CreateDataPattern(workspace, "B", 1, 1);
        DataPatternDefinition selectA =
            PatternDocumentEditor.CreateDataPattern(workspace, "Select A", 1, 1);
        DataPatternDefinition selectB =
            PatternDocumentEditor.CreateDataPattern(workspace, "Select B", 1, 1);
        DataPatternDefinition repeat =
            PatternDocumentEditor.CreateDataPattern(workspace, "Repeat", 2, 1);
        PatternDocumentEditor.SetSource(workspace, selectA, 0, 0, sourceA.Id);
        PatternDocumentEditor.SetSource(workspace, selectB, 0, 0, sourceB.Id);
        PatternDocumentEditor.SetNote(workspace, repeat, 0, 0, new StartPatternNote());
        PatternDocumentEditor.SetNote(workspace, repeat, 1, 0, new StartPatternNote());
        DataSequenceDefinition sequence = new(workspace.Document.AllocateObjectId(), "Order");
        sequence.Entries.Add(new SequenceEntry(selectA.Id));
        sequence.Entries.Add(new SequenceEntry(repeat.Id));
        sequence.Entries.Add(new SequenceEntry(selectB.Id));
        sequence.Entries.Add(new SequenceEntry(repeat.Id));
        workspace.Document.Add(sequence);
        PatternEditorContext context =
            PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
        PatternEditorControl editor = new(new Window(), workspace, context,
            () => { }, _ => { });
        var cells = Fields(editor, "_cellBorders");
        var tints = TintLayers(editor);
        AssertTint(tints[(1, 0)], sourceA.SourceColourArgb!.Value);
        AssertTint(tints[(2, 0)], sourceA.SourceColourArgb!.Value);
        AssertTint(tints[(4, 0)], sourceB.SourceColourArgb!.Value);
        AssertTint(tints[(5, 0)], sourceB.SourceColourArgb!.Value);
        Assert.That(tints[(0, 0)].Background, Is.SameAs(Brushes.Transparent),
            "a Source-only row is never a Note-start tint");
        IBrush unaffected = tints[(4, 0)].Background!;
        Grid previous = tints[(1, 0)];
        Border cell = cells[(1, 0)];
        uint audio = workspace.Document.AudioRevision;

        PatternDocumentEditor.SetSource(workspace, selectA, 0, 0, sourceB.Id);
        Invoke(editor, "RefreshUnderlyingCell", selectA, 0, 0);
        Assert.That(tints[(1, 0)], Is.SameAs(previous));
        Assert.That(cells[(1, 0)], Is.SameAs(cell),
            "a downstream change must not rebuild the tracker grid");
        AssertTint(tints[(1, 0)], sourceB.SourceColourArgb!.Value);
        AssertTint(tints[(2, 0)], sourceB.SourceColourArgb!.Value);
        Assert.That(tints[(4, 0)].Background, Is.SameAs(unaffected));
        Assert.That(tints[(5, 0)].Background, Is.SameAs(unaffected));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
        Assert.That(repeat.Grid[0, 0]!.SourceId.IsNone, Is.True,
            "projected tints cannot write inherited Source IDs into a Pattern");
    }

    private static DataPatternDefinition Source(SongDocument song, string name)
    {
        DataPatternDefinition source = new(song.AllocateObjectId(), name)
        { RowCount = 1, ChannelCount = 1 };
        song.Add(source);
        return source;
    }

    private static Dictionary<(int Row, int Channel), Grid> TintLayers(
        PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), Grid>)
            typeof(PatternEditorControl).GetField("_cellTintLayers",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), PatternEffectStripControl> EffectStrips(
        PatternEditorControl editor)
        => (Dictionary<(int Row, int Channel), PatternEffectStripControl>)
            typeof(PatternEditorControl).GetField("_effectStrips",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static Dictionary<(int Row, int Channel), Border> Fields(
        PatternEditorControl editor, string name)
        => (Dictionary<(int Row, int Channel), Border>)
            typeof(PatternEditorControl).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static void Invoke(PatternEditorControl editor, string name,
        params object[] args)
        => typeof(PatternEditorControl).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, args);

    private static void AssertTint(Grid field, uint argb)
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
