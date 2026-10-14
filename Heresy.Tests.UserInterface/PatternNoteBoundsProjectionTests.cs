using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.PatternEditing;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternNoteBoundsProjectionTests
{
    [Test]
    public void RepeatedSequenceEntriesAndSwitchesSeeTheSameLiveNonserializedBounds()
    {
        SongDocument song = new();
        DataPatternDefinition repeated = new((ObjectId)1U, "Repeated")
        { RowCount = 5, ChannelCount = 3 };
        repeated.Grid.GetOrCreateCell(4, 2).Note = new PatternNoteCut();
        song.Add(repeated);
        DataPatternDefinition other = new((ObjectId)2U, "Other")
        { RowCount = 2, ChannelCount = 1 };
        song.Add(other);
        DataSequenceDefinition sequence = new((ObjectId)3U, "Order");
        sequence.Entries.Add(new SequenceEntry(repeated.Id));
        sequence.Entries.Add(new SequenceEntry(other.Id));
        sequence.Entries.Add(new SequenceEntry(repeated.Id));
        song.Add(sequence);

        PatternEditorContext context = PatternEditorContext.ForSequence(
            song, sequence, 2);
        Assert.That(context.Segments[0].Pattern, Is.SameAs(repeated));
        Assert.That(context.Segments[2].Pattern, Is.SameAs(repeated));
        Assert.That(context.GetRow(0).Pattern.NoteEventBounds.RowCount, Is.EqualTo(5));
        Assert.That(context.GetRow(7).Pattern.NoteEventBounds.ChannelCount, Is.EqualTo(3));

        // Switching contexts, returning, or editing the shared live Pattern
        // cannot leave an occurrence-specific stale cached footprint.
        PatternEditorContext otherContext = PatternEditorContext.ForPattern(song, other);
        Assert.That(otherContext.GetRow(0).Pattern.NoteEventBounds.HasNoteEvents, Is.False);
        repeated.Grid[4, 2]!.Note = null;
        context.Refresh();
        Assert.That(context.GetRow(0).Pattern.NoteEventBounds.HasNoteEvents, Is.False);
        Assert.That(context.GetRow(7).Pattern.NoteEventBounds.RowCount, Is.EqualTo(1));
        repeated.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteOff();
        Assert.That(PatternEditorContext.ForPattern(song, repeated)
            .GetRow(2).Pattern.NoteEventBounds, Is.EqualTo(
                context.GetRow(9).Pattern.NoteEventBounds));
        Assert.That(context.GetRow(9).Pattern.NoteEventBounds,
            Is.EqualTo(new PatternNoteEventBounds(2, 1, true)));
    }
}
