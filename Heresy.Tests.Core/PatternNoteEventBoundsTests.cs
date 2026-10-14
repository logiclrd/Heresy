using System;
using System.IO;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class PatternNoteEventBoundsTests
{
    [Test]
    public void EmptyAndNonNoteCellsHaveMinimumFootprintWithoutEvents()
    {
        SongDocument song = new();
        DataPatternDefinition pattern = new((ObjectId)1U, "Bounds")
        { RowCount = 12, ChannelCount = 5 };
        song.Add(pattern);
        uint documentRevision = song.DocumentRevision;
        uint audioRevision = song.AudioRevision;

        AssertBounds(pattern, 0, 0, false);
        PatternCell trailing = pattern.Grid.GetOrCreateCell(11, 4);
        trailing.SourceId = (ObjectId)9U;
        trailing.Volume = 0.5;
        trailing.Effects.Add(new TrackerVolumeSlidePatternEffect(0x12));
        AssertBounds(pattern, 0, 0, false);

        pattern.Grid.GetOrCreateCell(3, 1).Note = new PatternNoteOff();
        pattern.Grid.GetOrCreateCell(2, 3).Note = new StartPatternNote();
        AssertBounds(pattern, 3, 3, true);
        pattern.Grid.GetOrCreateCell(10, 0).Note = new PatternNoteCut();
        AssertBounds(pattern, 10, 3, true);

        pattern.Grid[10, 0]!.Note = null;
        AssertBounds(pattern, 3, 3, true);
        pattern.Grid[2, 3]!.Note = null;
        AssertBounds(pattern, 3, 1, true);
        pattern.Grid[3, 1]!.Note = null;
        AssertBounds(pattern, 0, 0, false);
        Assert.That(song.DocumentRevision, Is.EqualTo(documentRevision));
        Assert.That(song.AudioRevision, Is.EqualTo(audioRevision),
            "reading/recomputing a display-only cache is never a song edit");
    }

    [Test]
    public void ReplacementsClearsAndSharedCellsKeepAccurateSubscriptions()
    {
        PatternGrid grid = new(9, 4);
        PatternCell first = new() { Note = new StartPatternNote() };
        grid[8, 3] = first;
        Assert.That(grid.NoteEventBounds, Is.EqualTo(new PatternNoteEventBounds(8, 3, true)));

        PatternCell replacement = new();
        grid[8, 3] = replacement;
        Assert.That(grid.NoteEventBounds.HasNoteEvents, Is.False);
        first.Note = new PatternNoteOff(); // detached cells must not count
        replacement.Note = new PatternNoteCut(); // attached mutable cell must invalidate
        Assert.That(grid.NoteEventBounds, Is.EqualTo(new PatternNoteEventBounds(8, 3, true)));
        grid.ClearCell(8, 3);
        Assert.That(grid.NoteEventBounds.HasNoteEvents, Is.False);

        PatternCell shared = new();
        grid[3, 0] = shared;
        grid[6, 2] = shared;
        shared.Note = new StartPatternNote();
        Assert.That(grid.NoteEventBounds, Is.EqualTo(new PatternNoteEventBounds(6, 2, true)));
        grid.ClearCell(6, 2);
        Assert.That(grid.NoteEventBounds, Is.EqualTo(new PatternNoteEventBounds(3, 0, true)));
        shared.Note = null; // still subscribed at (3,0)
        Assert.That(grid.NoteEventBounds.HasNoteEvents, Is.False);
        grid.ClearCell(3, 0);
        shared.Note = new PatternNoteOff(); // no longer a member
        Assert.That(grid.NoteEventBounds.HasNoteEvents, Is.False);
    }

    [Test]
    public void ShiftingIndividualOrAllChannelsAndTruncationInvalidateCachedBounds()
    {
        DataPatternDefinition pattern = new((ObjectId)2U, "Shifts")
        { RowCount = 8, ChannelCount = 4 };
        PatternGrid grid = pattern.Grid;
        grid.GetOrCreateCell(2, 1).Note = new StartPatternNote();
        grid.GetOrCreateCell(6, 3).Note = new PatternNoteOff();
        AssertBounds(pattern, 6, 3, true);

        grid.InsertRow(1, 1); // ch1 start: 2 -> 3; ch3 off stays at 6
        AssertBounds(pattern, 6, 3, true);
        Assert.That(grid[3, 1]?.Note, Is.TypeOf<StartPatternNote>());
        grid.InsertRow(5, null); // ch3 off: 6 -> 7
        AssertBounds(pattern, 7, 3, true);
        grid.DeleteRow(7, 3); // remove highest event, retaining ch1
        AssertBounds(pattern, 3, 1, true);
        grid.DeleteRow(0, null); // ch1: 3 -> 2
        AssertBounds(pattern, 2, 1, true);

        grid.GetOrCreateCell(7, 3).Note = new PatternNoteCut();
        AssertBounds(pattern, 7, 3, true);
        pattern.RowCount = 6; // DataPatternDefinition dimension hook resizes grid
        AssertBounds(pattern, 2, 1, true);
        pattern.ChannelCount = 1;
        AssertBounds(pattern, 0, 0, false);
        pattern.RowCount = 10;
        pattern.ChannelCount = 4;
        AssertBounds(pattern, 0, 0, false);
        grid.GetOrCreateCell(9, 3).Note = new StartPatternNote();
        AssertBounds(pattern, 9, 3, true);
    }

    [Test]
    public void JsonAndPackageRebuildOnlyFromPersistedNotes()
    {
        SongDocument song = new();
        DataPatternDefinition pattern = new((ObjectId)1U, "Serialized")
        { RowCount = 8, ChannelCount = 4 };
        song.Add(pattern);
        pattern.Grid.GetOrCreateCell(7, 2).Note = new PatternNoteOff();
        AssertBounds(pattern, 7, 2, true);
        string jsonPath = Path.Combine(Path.GetTempPath(),
            "heresy-pattern-bounds-" + Guid.NewGuid().ToString("N") + ".hm.json");
        string json = SongDocumentJson.Serialize(song, jsonPath);
        Assert.That(json, Does.Not.Contain("noteEventBounds").IgnoreCase);
        Assert.That(json, Does.Not.Contain("lastNoteEvent").IgnoreCase);
        SongDocument loaded = SongDocumentJson.Deserialize(json, jsonPath);
        DataPatternDefinition copy = (DataPatternDefinition)loaded.Objects[pattern.Id];
        AssertBounds(copy, 7, 2, true);
        copy.Grid[7, 2]!.Note = null;
        AssertBounds(copy, 0, 0, false);
        AssertBounds(pattern, 7, 2, true);

        string packagePath = Path.Combine(Path.GetTempPath(),
            "heresy-pattern-bounds-" + Guid.NewGuid().ToString("N") + ".hm");
        try
        {
            SongDocumentPackage.Save(packagePath, song);
            SongDocument packaged = SongDocumentPackage.Load(packagePath);
            DataPatternDefinition roundtrip = (DataPatternDefinition)packaged.Objects[pattern.Id];
            AssertBounds(roundtrip, 7, 2, true);
            roundtrip.Grid.ClearCell(7, 2);
            AssertBounds(roundtrip, 0, 0, false);
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
        }
    }

    private static void AssertBounds(
        DataPatternDefinition pattern, int lastRow, int lastChannel, bool hasNotes)
    {
        PatternNoteEventBounds bounds = pattern.NoteEventBounds;
        Assert.Multiple(() =>
        {
            Assert.That(bounds.LastRow, Is.EqualTo(lastRow));
            Assert.That(bounds.LastChannel, Is.EqualTo(lastChannel));
            Assert.That(bounds.HasNoteEvents, Is.EqualTo(hasNotes));
            Assert.That(bounds.RowCount, Is.EqualTo(lastRow + 1));
            Assert.That(bounds.ChannelCount, Is.EqualTo(lastChannel + 1));
            Assert.That(pattern.NoteEventBounds, Is.EqualTo(bounds),
                "repeated projected reads must reuse current derived bounds");
        });
    }
}
